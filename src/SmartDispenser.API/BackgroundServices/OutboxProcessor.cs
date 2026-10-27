using Microsoft.EntityFrameworkCore;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;
using SmartDispenser.Infrastructure.Data;

namespace SmartDispenser.API.BackgroundServices;

/// <summary>Delivery is at-least-once: a consumer can see the same message twice.</summary>
public sealed class OutboxProcessor(IServiceScopeFactory scopeFactory, ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollingInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DeliverDueMessagesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox processing pass failed");
            }
        }
    }

    private async Task DeliverDueMessagesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<IDispenserNotifier>();

        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages
            .Where(m => m.Status == OutboxMessageStatus.Pending && m.NextRetryAt <= now)
            .OrderBy(m => m.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var message in messages)
        {
            try
            {
                await DeliverAsync(message, notifier, ct);
                message.MarkProcessed();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.RecordFailure(ex.Message);
                logger.LogWarning(ex, "Outbox message {Id} ({EventType}) failed attempt {Attempt}, now {Status}",
                    message.Id, message.EventType, message.RetryCount, message.Status);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static Task DeliverAsync(OutboxMessage message, IDispenserNotifier notifier, CancellationToken ct) =>
        message.EventType switch
        {
            OutboxEvents.DispenseResult => notifier.PublishDispenseResultAsync(message.Deserialize<DispenseResultNotification>(), ct),
            _ => throw new InvalidOperationException($"No delivery route for outbox event '{message.EventType}'."),
        };
}
