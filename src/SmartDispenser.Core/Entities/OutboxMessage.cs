using System.Text.Json;

namespace SmartDispenser.Core.Entities;

/// <summary>
/// Outbox pattern entity — ensures at-least-once delivery of domain events
/// to external systems (webhooks, message queues) even in case of crashes.
///
/// Design: events are written atomically with the business transaction.
/// A background processor reads unprocessed messages and delivers them,
/// retrying on failure with exponential backoff.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public OutboxMessageStatus Status { get; private set; }
    public int RetryCount { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset? NextRetryAt { get; private set; }

    // Maximum delivery attempts before marking as Dead Letter
    public const int MaxRetries = 5;

    private OutboxMessage() { }

    public static OutboxMessage Create<TPayload>(string eventType, TPayload payload)
    {
        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload),
            Status = OutboxMessageStatus.Pending,
            RetryCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            NextRetryAt = DateTimeOffset.UtcNow,
        };
    }

    public TPayload Deserialize<TPayload>() =>
        JsonSerializer.Deserialize<TPayload>(Payload)
        ?? throw new InvalidOperationException($"Cannot deserialize payload for event '{EventType}'.");

    public void MarkProcessed()
    {
        Status = OutboxMessageStatus.Processed;
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    public void RecordFailure(string error)
    {
        RetryCount++;
        LastError = error;

        if (RetryCount >= MaxRetries)
        {
            Status = OutboxMessageStatus.DeadLetter;
        }
        else
        {
            // Exponential backoff: 5s, 25s, 125s, 625s
            var backoffSeconds = Math.Pow(5, RetryCount);
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(backoffSeconds);
            Status = OutboxMessageStatus.Pending;
        }
    }

    public bool IsReadyToProcess() =>
        Status == OutboxMessageStatus.Pending
        && (NextRetryAt == null || NextRetryAt <= DateTimeOffset.UtcNow);
}

public enum OutboxMessageStatus { Pending, Processing, Processed, DeadLetter }
