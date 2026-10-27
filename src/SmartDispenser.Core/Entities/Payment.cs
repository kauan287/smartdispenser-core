namespace SmartDispenser.Core.Entities;

public sealed class Payment
{
    public Guid Id { get; private set; }
    public Guid DispenseRequestId { get; private set; }
    public string ExternalTransactionId { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public PaymentStatus Status { get; private set; }
    public string GatewayProvider { get; private set; } = string.Empty;
    public string? GatewayResponse { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Payment() { }

    public static Payment CreateFromWebhook(
        Guid dispenseRequestId,
        string externalTransactionId,
        string idempotencyKey,
        decimal amount,
        string currency,
        string gatewayProvider)
    {
        return new Payment
        {
            Id = Guid.NewGuid(),
            DispenseRequestId = dispenseRequestId,
            ExternalTransactionId = externalTransactionId,
            IdempotencyKey = idempotencyKey,
            Amount = amount,
            Currency = currency,
            Status = PaymentStatus.Pending,
            GatewayProvider = gatewayProvider,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Confirm(string? gatewayResponse = null)
    {
        Status = PaymentStatus.Confirmed;
        GatewayResponse = gatewayResponse;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(string reason)
    {
        Status = PaymentStatus.Failed;
        GatewayResponse = reason;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
