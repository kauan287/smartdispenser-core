using MediatR;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Core.Commands.UpdateMachineState;

// ── Command ───────────────────────────────────────────────────────────────────

public sealed record UpdateMachineStateCommand(
    Guid DispenserId,
    MachineStateEvent Event,
    int? InventoryCount = null,
    Guid? CompletedRequestId = null,
    string? ErrorMessage = null,
    string? FirmwareVersion = null
) : IRequest<Unit>;

public enum MachineStateEvent
{
    Heartbeat,
    DispenseCompleted,
    DispenseFailed,
    InventoryUpdated,
    Online,
    Offline,
    ErrorReset
}

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class UpdateMachineStateHandler : IRequestHandler<UpdateMachineStateCommand, Unit>
{
    private readonly IDispenserRepository _dispenserRepo;
    private readonly IDispenseRequestRepository _requestRepo;
    private readonly IDispenserHubContext _hubContext;
    private readonly ILogger<UpdateMachineStateHandler> _logger;

    public UpdateMachineStateHandler(
        IDispenserRepository dispenserRepo,
        IDispenseRequestRepository requestRepo,
        IDispenserHubContext hubContext,
        ILogger<UpdateMachineStateHandler> logger)
    {
        _dispenserRepo = dispenserRepo;
        _requestRepo = requestRepo;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<Unit> Handle(UpdateMachineStateCommand cmd, CancellationToken ct)
    {
        var dispenser = await _dispenserRepo.GetByIdAsync(cmd.DispenserId, ct);
        if (dispenser is null)
        {
            _logger.LogWarning("Received {Event} for unknown dispenser {Id}", cmd.Event, cmd.DispenserId);
            return Unit.Value;
        }

        switch (cmd.Event)
        {
            case MachineStateEvent.Heartbeat:
                dispenser.RecordHeartbeat();
                break;

            case MachineStateEvent.Online:
                dispenser.GoOnline(cmd.FirmwareVersion);
                break;

            case MachineStateEvent.Offline:
                dispenser.GoOffline();
                break;

            case MachineStateEvent.InventoryUpdated when cmd.InventoryCount.HasValue:
                dispenser.UpdateInventory(cmd.InventoryCount.Value);
                break;

            case MachineStateEvent.DispenseCompleted when cmd.CompletedRequestId.HasValue:
                dispenser.CompleteDispensing();
                var completedRequest = await _requestRepo.GetByIdAsync(cmd.CompletedRequestId.Value, ct);
                if (completedRequest is not null)
                {
                    completedRequest.MarkCompleted();
                    await _requestRepo.UpdateAsync(completedRequest, ct);
                }
                break;

            case MachineStateEvent.DispenseFailed when cmd.CompletedRequestId.HasValue:
                var error = cmd.ErrorMessage ?? "Machine reported dispensing failure";
                dispenser.FailDispensing(error);
                var failedRequest = await _requestRepo.GetByIdAsync(cmd.CompletedRequestId.Value, ct);
                if (failedRequest is not null)
                {
                    failedRequest.MarkFailed(error);
                    await _requestRepo.UpdateAsync(failedRequest, ct);
                }
                break;

            case MachineStateEvent.ErrorReset:
                dispenser.ResetFromError();
                break;
        }

        await _dispenserRepo.UpdateAsync(dispenser, ct);

        // Broadcast real-time state to Blazor dashboard via SignalR
        await _hubContext.BroadcastStateUpdateAsync(new DispenserStateDto
        {
            Id = dispenser.Id,
            Name = dispenser.Name,
            Location = dispenser.Location,
            Status = dispenser.Status.ToString(),
            InventoryCount = dispenser.InventoryCount,
            InventoryCapacity = dispenser.InventoryCapacity,
            InventoryPercentage = dispenser.InventoryPercentage,
            LastHeartbeatAt = dispenser.LastHeartbeatAt,
            LastDispenseAt = dispenser.LastDispenseAt,
            FirmwareVersion = dispenser.FirmwareVersion,
        }, ct);

        _logger.LogDebug("Dispenser {Name} state updated: {Event}", dispenser.Name, cmd.Event);
        return Unit.Value;
    }
}
