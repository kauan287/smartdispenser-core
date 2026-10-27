using Microsoft.AspNetCore.SignalR;
using SmartDispenser.API.Hubs;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.API.SignalR;

/// <summary>
/// Sends commands to machines through the SignalR hub and raises in-process events for the
/// Blazor dashboard. The dashboard already runs inside this process over its own circuit,
/// so it subscribes here instead of opening a second SignalR connection back to the server.
/// </summary>
public sealed class DispenserNotifier(IHubContext<DispenserHub, IMachineClient> hub) : IDispenserNotifier
{
    public event Action<DispenserStateDto>? StateChanged;
    public event Action<DispenseResultNotification>? DispenseResultPublished;

    public Task SendDispenseCommandAsync(Guid dispenserId, DispenseCommand command, CancellationToken ct = default) =>
        hub.Clients.Group(DispenserHub.MachineGroup(dispenserId)).ReceiveDispenseCommand(command);

    public Task PublishStateAsync(DispenserStateDto state, CancellationToken ct = default)
    {
        StateChanged?.Invoke(state);
        return Task.CompletedTask;
    }

    public Task PublishDispenseResultAsync(DispenseResultNotification result, CancellationToken ct = default)
    {
        DispenseResultPublished?.Invoke(result);
        return Task.CompletedTask;
    }
}
