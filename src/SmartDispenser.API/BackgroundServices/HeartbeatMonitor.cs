using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.Commands.UpdateMachineState;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.API.BackgroundServices;

/// <summary>
/// Marks machines Offline after 90 s without a heartbeat. SignalR does not always raise
/// OnDisconnectedAsync promptly when a machine loses power or network. Also interrupts a
/// dispense the machine has not confirmed within <c>Dispense:DispatchDeadline</c> (default 2 minutes)
/// and moves the machine to Error.
/// </summary>
public sealed class HeartbeatMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeartbeatMonitor> _logger;
    private readonly TimeSpan _dispatchDeadline;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(90);

    public HeartbeatMonitor(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<HeartbeatMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _dispatchDeadline = configuration.GetValue("Dispense:DispatchDeadline", TimeSpan.FromMinutes(2));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Heartbeat monitor started. Checking every {Interval}s, stale threshold: {Threshold}s, dispatch deadline: {Deadline}s",
            CheckInterval.TotalSeconds, StaleThreshold.TotalSeconds, _dispatchDeadline.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken);

            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Heartbeat monitor encountered an error");
            }
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispenserRepo = scope.ServiceProvider.GetRequiredService<IDispenserRepository>();
        var requestRepo = scope.ServiceProvider.GetRequiredService<IDispenseRequestRepository>();

        foreach (var request in await requestRepo.GetDispatchedBeforeAsync(DateTimeOffset.UtcNow - _dispatchDeadline, ct))
        {
            _logger.LogWarning(
                "Machine {DispenserId} did not confirm dispense request {RequestId} within {Deadline}s. Marking it Interrupted.",
                request.DispenserId, request.Id, _dispatchDeadline.TotalSeconds);
            await Send(new UpdateMachineStateCommand(request.DispenserId, MachineStateEvent.DispatchExpired, RequestId: request.Id), ct);
        }

        var allMachines = await dispenserRepo.GetAllAsync(ct);
        var staleMachines = allMachines
            .Where(d => d.Status != Core.Entities.DispenserStatus.Offline
                     && d.IsStale(StaleThreshold))
            .ToList();

        foreach (var machine in staleMachines)
        {
            _logger.LogWarning(
                "Machine '{Name}' ({Id}) has not sent a heartbeat in {Elapsed:F0}s. Marking Offline.",
                machine.Name, machine.Id,
                (DateTimeOffset.UtcNow - machine.LastHeartbeatAt).TotalSeconds);

            await Send(new UpdateMachineStateCommand(machine.Id, MachineStateEvent.Offline), ct);
        }

        if (staleMachines.Count > 0)
        {
            _logger.LogWarning("{Count} machine(s) marked offline due to missed heartbeats", staleMachines.Count);
        }
    }

    /// <summary>
    /// Each command gets its own scope, so a save that loses a race leaves no tracked changes
    /// behind for the next machine's save to trip over.
    /// </summary>
    private async Task Send(UpdateMachineStateCommand command, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "{Event} for machine {DispenserId} not applied", command.Event, command.DispenserId);
        }
    }
}
