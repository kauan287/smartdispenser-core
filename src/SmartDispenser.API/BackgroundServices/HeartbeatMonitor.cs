using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.Commands.UpdateMachineState;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.API.BackgroundServices;

/// <summary>
/// Monitors machine connectivity health by detecting stale heartbeats.
///
/// Every 30 seconds, checks all machines whose LastHeartbeatAt is older
/// than the configured threshold. Machines that miss heartbeats are
/// transitioned to Offline status.
///
/// This catches machines that disconnect without sending OnDisconnectedAsync
/// (e.g. network cable pull, power loss, OS crash).
/// </summary>
public sealed class HeartbeatMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeartbeatMonitor> _logger;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(90);

    public HeartbeatMonitor(IServiceScopeFactory scopeFactory, ILogger<HeartbeatMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Heartbeat monitor started. Checking every {Interval}s, stale threshold: {Threshold}s",
            CheckInterval.TotalSeconds, StaleThreshold.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken);

            try
            {
                await CheckForStaleMachinesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Heartbeat monitor encountered an error");
            }
        }
    }

    private async Task CheckForStaleMachinesAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispenserRepo = scope.ServiceProvider.GetRequiredService<IDispenserRepository>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

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

            await mediator.Send(new UpdateMachineStateCommand(machine.Id, MachineStateEvent.Offline), ct);
        }

        if (staleMachines.Count > 0)
        {
            _logger.LogWarning("{Count} machine(s) marked offline due to missed heartbeats", staleMachines.Count);
        }
    }
}
