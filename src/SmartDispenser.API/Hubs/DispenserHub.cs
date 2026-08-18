using MediatR;
using Microsoft.AspNetCore.SignalR;
using SmartDispenser.Core.Commands.UpdateMachineState;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.API.Hubs;

/// <summary>
/// Client-side method contract for the typed SignalR hub.
/// Defines what the SERVER can call on each CONNECTED CLIENT.
/// </summary>
public interface IDispenserClient
{
    Task ReceiveCommand(DispenserCommand command, CancellationToken ct = default);
    Task MachineStateUpdated(DispenserStateDto state, CancellationToken ct = default);
    Task DispenseResultReceived(DispenseResultNotification result, CancellationToken ct = default);
}

/// <summary>
/// SignalR Hub for bidirectional machine ↔ server communication.
///
/// Topology:
///   - Each physical machine connects and joins its own group: Group("{machineId}")
///   - Blazor dashboard clients join the "dashboard" group
///   - Server sends typed commands to machine groups
///   - Machines send telemetry events back (heartbeat, inventory, dispense results)
///
/// Authentication note: In production, machines authenticate with a signed JWT.
/// The machine ID is extracted from the JWT "sub" claim. For the portfolio demo,
/// the machine ID is passed as a query parameter (?machineId=...).
/// </summary>
public sealed class DispenserHub : Hub<IDispenserClient>
{
    private readonly IMediator _mediator;
    private readonly ILogger<DispenserHub> _logger;

    public DispenserHub(IMediator mediator, ILogger<DispenserHub> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    // ── Connection lifecycle ───────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var machineId = Context.GetHttpContext()?.Request.Query["machineId"].ToString();
        var isDashboard = Context.GetHttpContext()?.Request.Query["role"].ToString() == "dashboard";

        if (isDashboard)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
            _logger.LogInformation("Dashboard client connected: {ConnectionId}", Context.ConnectionId);
        }
        else if (Guid.TryParse(machineId, out var dispenserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, dispenserId.ToString());
            Context.Items["dispenserId"] = dispenserId;

            _logger.LogInformation("Machine {MachineId} connected: {ConnectionId}", dispenserId, Context.ConnectionId);

            // Notify application layer
            await _mediator.Send(new UpdateMachineStateCommand(dispenserId, MachineStateEvent.Online));
        }
        else
        {
            _logger.LogWarning("Connection rejected: missing or invalid machineId/role. ConnectionId={Id}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("dispenserId", out var id) && id is Guid dispenserId)
        {
            _logger.LogInformation(
                "Machine {MachineId} disconnected. Reason: {Reason}",
                dispenserId, exception?.Message ?? "Clean disconnect");

            await _mediator.Send(new UpdateMachineStateCommand(dispenserId, MachineStateEvent.Offline));
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ── Machine → Server messages (called by physical machines) ───────────────

    /// <summary>
    /// Machine sends periodic heartbeat to confirm it is alive.
    /// HeartbeatMonitor background service marks machines as Offline
    /// if no heartbeat is received within the threshold.
    /// </summary>
    public async Task SendHeartbeat(string machineId)
    {
        if (!Guid.TryParse(machineId, out var dispenserId))
            return;

        await _mediator.Send(new UpdateMachineStateCommand(dispenserId, MachineStateEvent.Heartbeat));
    }

    /// <summary>Machine reports current inventory level after dispensing or manual restock.</summary>
    public async Task ReportInventory(string machineId, int count)
    {
        if (!Guid.TryParse(machineId, out var dispenserId))
            return;

        _logger.LogInformation("Machine {Id} reported inventory: {Count}", machineId, count);
        await _mediator.Send(new UpdateMachineStateCommand(
            dispenserId, MachineStateEvent.InventoryUpdated, InventoryCount: count));
    }

    /// <summary>Machine confirms that a dispense command was executed successfully.</summary>
    public async Task ConfirmDispense(string machineId, string requestId)
    {
        if (!Guid.TryParse(machineId, out var dispenserId) ||
            !Guid.TryParse(requestId, out var dispenseRequestId))
            return;

        _logger.LogInformation(
            "Machine {MachineId} confirmed dispense of request {RequestId}", machineId, requestId);

        await _mediator.Send(new UpdateMachineStateCommand(
            dispenserId,
            MachineStateEvent.DispenseCompleted,
            CompletedRequestId: dispenseRequestId));
    }

    /// <summary>Machine reports that it could not complete a dispense command.</summary>
    public async Task ReportDispenseFailure(string machineId, string requestId, string errorMessage)
    {
        if (!Guid.TryParse(machineId, out var dispenserId) ||
            !Guid.TryParse(requestId, out var dispenseRequestId))
            return;

        _logger.LogError(
            "Machine {MachineId} failed to dispense request {RequestId}: {Error}",
            machineId, requestId, errorMessage);

        await _mediator.Send(new UpdateMachineStateCommand(
            dispenserId,
            MachineStateEvent.DispenseFailed,
            CompletedRequestId: dispenseRequestId,
            ErrorMessage: errorMessage));
    }
}
