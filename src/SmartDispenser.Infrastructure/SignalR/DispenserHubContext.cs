using Microsoft.AspNetCore.SignalR;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Infrastructure.SignalR;

/// <summary>
/// Typed SignalR hub proxy — wraps IHubContext to provide a clean
/// interface for the application layer, completely decoupling it
/// from the SignalR infrastructure package.
/// </summary>
public sealed class DispenserHubContext : IDispenserHubContext
{
    private readonly IHubContext<API.Hubs.DispenserHub, IDispenserClient> _hubContext;

    public DispenserHubContext(IHubContext<API.Hubs.DispenserHub, IDispenserClient> hubContext)
    {
        _hubContext = hubContext;
    }

    /// <summary>Sends a typed command to a specific machine group.</summary>
    public async Task SendCommandAsync(Guid dispenserId, DispenserCommand command, CancellationToken ct = default)
    {
        await _hubContext.Clients
            .Group(dispenserId.ToString())
            .ReceiveCommand(command, ct);
    }

    /// <summary>Broadcasts state update to the "dashboard" group (Blazor UI subscribers).</summary>
    public async Task BroadcastStateUpdateAsync(DispenserStateDto state, CancellationToken ct = default)
    {
        await _hubContext.Clients
            .Group("dashboard")
            .MachineStateUpdated(state, ct);
    }

    /// <summary>Notifies dashboard clients of a dispense result.</summary>
    public async Task NotifyDispenseResultAsync(Guid dispenserId, DispenseResultNotification result, CancellationToken ct = default)
    {
        await _hubContext.Clients
            .Group("dashboard")
            .DispenseResultReceived(result, ct);
    }
}
