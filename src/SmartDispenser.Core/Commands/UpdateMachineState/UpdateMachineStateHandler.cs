using MediatR;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Core.Commands.UpdateMachineState;

public sealed record UpdateMachineStateCommand(
    Guid DispenserId,
    MachineStateEvent Event,
    int? InventoryCount = null,
    Guid? RequestId = null,
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
    Offline
}

public sealed class UpdateMachineStateHandler(
    IDispenserRepository dispenserRepo,
    IDispenseRequestRepository requestRepo,
    IDispenserNotifier notifier,
    IUnitOfWork unitOfWork,
    ILogger<UpdateMachineStateHandler> logger)
    : IRequestHandler<UpdateMachineStateCommand, Unit>
{
    public async Task<Unit> Handle(UpdateMachineStateCommand cmd, CancellationToken ct)
    {
        var dispenser = await dispenserRepo.GetByIdAsync(cmd.DispenserId, ct);
        if (dispenser is null)
        {
            logger.LogWarning("Received {Event} for unknown dispenser {Id}", cmd.Event, cmd.DispenserId);
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
                var interrupted = dispenser.CurrentDispenseRequestId;
                dispenser.GoOffline();
                if (interrupted is not null)
                    await UpdateRequest(interrupted.Value, r => r.MarkFailed("Machine went offline during dispense."), ct);
                break;

            case MachineStateEvent.InventoryUpdated:
                dispenser.UpdateInventory(Required(cmd.InventoryCount));
                break;

            case MachineStateEvent.DispenseCompleted:
                var completedId = Required(cmd.RequestId);
                dispenser.CompleteDispensing(completedId);
                await UpdateRequest(completedId, r => r.MarkCompleted(), ct);
                break;

            case MachineStateEvent.DispenseFailed:
                var failedId = Required(cmd.RequestId);
                dispenser.FailDispensing(failedId);
                await UpdateRequest(failedId, r => r.MarkFailed(cmd.ErrorMessage ?? "Machine reported a dispensing failure."), ct);
                break;
        }

        await unitOfWork.SaveChangesAsync(ct);
        await notifier.PublishStateAsync(DispenserStateDto.From(dispenser), ct);
        return Unit.Value;

        T Required<T>(T? value) where T : struct =>
            value ?? throw new ArgumentException($"{cmd.Event} requires a value it did not receive.");
    }

    private async Task UpdateRequest(Guid requestId, Action<DispenseRequest> transition, CancellationToken ct)
    {
        var request = await requestRepo.GetByIdAsync(requestId, ct)
            ?? throw new InvalidOperationException($"Dispense request {requestId} not found.");
        transition(request);
    }
}
