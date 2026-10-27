using Microsoft.AspNetCore.SignalR;
using SmartDispenser.API.Hubs;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.API.SignalR;

/// <summary>
/// The Blazor dashboard runs in this process, so it subscribes to these events instead of
/// opening a SignalR client to the server's public URL, which is unreachable behind port mapping.
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
