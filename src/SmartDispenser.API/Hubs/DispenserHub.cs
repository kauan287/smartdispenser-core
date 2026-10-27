using MediatR;
using Microsoft.AspNetCore.SignalR;
using SmartDispenser.Core.Commands.UpdateMachineState;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.API.Hubs;

/// <summary>Methods the server calls on a connected machine.</summary>
public interface IMachineClient
{
    Task ReceiveDispenseCommand(DispenseCommand command);
}

/// <summary>
/// SignalR endpoint for vending machines. A machine connects with <c>?machineId={guid}</c> and
/// every later call acts on that machine only. This demo trusts the query string; a production
/// deployment would take the machine id from an authenticated claim in OnConnectedAsync.
/// </summary>
public sealed class DispenserHub(IMediator mediator, ILogger<DispenserHub> logger) : Hub<IMachineClient>
{
    private const string MachineIdKey = "machineId";

    public static string MachineGroup(Guid dispenserId) => $"machine-{dispenserId}";

    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.GetHttpContext()?.Request.Query["machineId"], out var dispenserId))
        {
            logger.LogWarning("Rejected hub connection {ConnectionId} without a valid machineId", Context.ConnectionId);
            Context.Abort();
            return;
        }

        Context.Items[MachineIdKey] = dispenserId;
        await Groups.AddToGroupAsync(Context.ConnectionId, MachineGroup(dispenserId));
        await mediator.Send(new UpdateMachineStateCommand(dispenserId, MachineStateEvent.Online));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(MachineIdKey, out var id) && id is Guid dispenserId)
            await mediator.Send(new UpdateMachineStateCommand(dispenserId, MachineStateEvent.Offline));
        await base.OnDisconnectedAsync(exception);
    }

    public Task SendHeartbeat() =>
        Apply(MachineStateEvent.Heartbeat);

    public Task ReportInventory(int count) =>
        Apply(MachineStateEvent.InventoryUpdated, inventoryCount: count);

    public Task ConfirmDispense(Guid requestId) =>
        Apply(MachineStateEvent.DispenseCompleted, requestId: requestId);

    public Task ReportDispenseFailure(Guid requestId, string errorMessage) =>
        Apply(MachineStateEvent.DispenseFailed, requestId: requestId, errorMessage: errorMessage);

    private async Task Apply(MachineStateEvent evt, int? inventoryCount = null, Guid? requestId = null, string? errorMessage = null)
    {
        if (!Context.Items.TryGetValue(MachineIdKey, out var id) || id is not Guid dispenserId)
            throw new HubException("This connection is not bound to a machine.");
        try
        {
            await mediator.Send(new UpdateMachineStateCommand(dispenserId, evt, inventoryCount, requestId, errorMessage));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new HubException(ex.Message);
        }
    }
}
