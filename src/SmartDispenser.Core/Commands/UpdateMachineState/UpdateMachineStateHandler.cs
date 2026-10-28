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
    Offline,
    DispatchExpired
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
                    (await LoadRequest(interrupted.Value, ct)).MarkInterrupted();
                break;

            case MachineStateEvent.DispatchExpired:
                var expired = await LoadOwnRequest(Required(cmd.RequestId));
                dispenser.FailDispensing(expired.Id);
                expired.MarkInterrupted();
                break;

            case MachineStateEvent.InventoryUpdated:
                dispenser.UpdateInventory(Required(cmd.InventoryCount));
                break;

            case MachineStateEvent.DispenseCompleted:
                var completed = await LoadOwnRequest(Required(cmd.RequestId));
                if (completed.Status == DispenseRequestStatus.Interrupted)
                    dispenser.RecordLateDispense();
                else
                    dispenser.CompleteDispensing(completed.Id);
                completed.MarkCompleted();
                break;

            case MachineStateEvent.DispenseFailed:
                var failed = await LoadOwnRequest(Required(cmd.RequestId));
                if (failed.Status != DispenseRequestStatus.Interrupted)
                    dispenser.FailDispensing(failed.Id);
                failed.MarkFailed(cmd.ErrorMessage ?? "Machine reported a dispensing failure.");
                break;
        }

        await unitOfWork.SaveChangesAsync(ct);
        await notifier.PublishStateAsync(DispenserStateDto.From(dispenser), ct);
        return Unit.Value;

        T Required<T>(T? value) where T : struct =>
            value ?? throw new ArgumentException($"{cmd.Event} requires a value it did not receive.");

        async Task<DispenseRequest> LoadOwnRequest(Guid requestId)
        {
            var request = await LoadRequest(requestId, ct);
            return request.DispenserId == dispenser.Id
                ? request
                : throw new InvalidOperationException($"Dispense request {requestId} does not belong to dispenser '{dispenser.Name}'.");
        }
    }

    private async Task<DispenseRequest> LoadRequest(Guid requestId, CancellationToken ct) =>
        await requestRepo.GetByIdAsync(requestId, ct)
            ?? throw new InvalidOperationException($"Dispense request {requestId} not found.");
}
