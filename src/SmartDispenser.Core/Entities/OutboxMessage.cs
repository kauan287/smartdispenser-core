using System.Text.Json;

namespace SmartDispenser.Core.Entities;

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
            var backoffSeconds = Math.Pow(5, RetryCount);
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(backoffSeconds);
            Status = OutboxMessageStatus.Pending;
        }
    }
}

public enum OutboxMessageStatus { Pending, Processed, DeadLetter }

public static class OutboxEvents
{
    /// <summary>Payload: <see cref="Interfaces.DispenseResultNotification"/>.</summary>
    public const string DispenseResult = "DispenseResult";
}
