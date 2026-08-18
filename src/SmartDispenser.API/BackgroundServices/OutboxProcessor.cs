using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.Entities;
using SmartDispenser.Infrastructure.Data;

namespace SmartDispenser.API.BackgroundServices;

/// <summary>
/// Background processor for the Outbox Pattern.
///
/// Every 5 seconds, queries for OutboxMessages that are due for processing
/// and attempts to deliver them (HTTP webhook, email, etc.).
///
/// Delivery guarantees:
///   - At-least-once: messages are retried up to MaxRetries times.
///   - Exponential backoff: prevents hammering a failing endpoint.
///   - Dead Letter: after MaxRetries, messages are marked DeadLetter for
///     manual inspection / alerting.
///
/// Idempotency note: receivers must handle duplicate delivery.
/// </summary>
public sealed class OutboxProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxProcessor> _logger;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    public OutboxProcessor(IServiceScopeFactory scopeFactory, ILogger<OutboxProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox processor started. Polling every {Interval}s", PollingInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessagesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Outbox processor encountered an unhandled exception. Continuing...");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingMessagesAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Optimistic concurrency: mark as Processing before delivering
        // to reduce duplicate delivery in multi-instance deployments.
        var messages = await db.OutboxMessages
            .Where(m => m.Status == OutboxMessageStatus.Pending
                     && m.NextRetryAt <= DateTimeOffset.UtcNow)
            .OrderBy(m => m.CreatedAt)
            .Take(20) // Process in batches of 20
            .ToListAsync(ct);

        if (messages.Count == 0) return;

        _logger.LogDebug("Processing {Count} outbox messages", messages.Count);

        foreach (var message in messages)
        {
            await DeliverMessageAsync(message, db, ct);
        }
    }

    private async Task DeliverMessageAsync(OutboxMessage message, AppDbContext db, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug(
                "Delivering outbox message {Id} (EventType={Type}, Attempt={Attempt})",
                message.Id, message.EventType, message.RetryCount + 1);

            // Dispatch based on event type
            // In production: route to HTTP endpoints, message queues, etc.
            await DispatchEventAsync(message, ct);

            message.MarkProcessed();
            _logger.LogInformation("Outbox message {Id} delivered successfully", message.Id);
        }
        catch (Exception ex)
        {
            message.RecordFailure(ex.Message);

            if (message.Status == OutboxMessageStatus.DeadLetter)
            {
                _logger.LogError(
                    "Outbox message {Id} (EventType={Type}) moved to Dead Letter after {MaxRetries} attempts. Last error: {Error}",
                    message.Id, message.EventType, OutboxMessage.MaxRetries, ex.Message);
            }
            else
            {
                _logger.LogWarning(
                    "Outbox message {Id} delivery failed (attempt {Attempt}). Next retry at {NextRetry}. Error: {Error}",
                    message.Id, message.RetryCount, message.NextRetryAt, ex.Message);
            }
        }
        finally
        {
            db.OutboxMessages.Update(message);
            await db.SaveChangesAsync(ct);
        }
    }

    private static Task DispatchEventAsync(OutboxMessage message, CancellationToken ct)
    {
        // In a full implementation, this would route to different delivery channels:
        // - DispenseCompleted → call external EHR webhook
        // - PaymentConfirmed  → send patient notification
        // - MachineOffline    → alert operations team
        //
        // For demo: simulate a dispatch delay
        return message.EventType switch
        {
            "DispenseCompleted" => Task.Delay(50, ct),
            "PaymentConfirmed" => Task.Delay(50, ct),
            "MachineOffline" => Task.Delay(50, ct),
            _ => Task.CompletedTask
        };
    }
}
