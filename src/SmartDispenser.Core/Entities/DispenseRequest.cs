namespace SmartDispenser.Core.Entities;

/// <summary>
/// Tracks the full lifecycle of a single dispense request,
/// from prescription verification through payment and physical dispensing.
/// </summary>
public sealed class DispenseRequest
{
    public Guid Id { get; private set; }
    public Guid DispenserId { get; private set; }
    public string PrescriptionId { get; private set; } = string.Empty;
    public string PatientIdentifier { get; private set; } = string.Empty;
    public string Medication { get; private set; } = string.Empty;
    public string Dosage { get; private set; } = string.Empty;
    public DispenseRequestStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public Guid? PaymentId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    // Correlation ID for distributed tracing across services
    public string CorrelationId { get; private set; } = string.Empty;

    private DispenseRequest() { }

    public static DispenseRequest Create(
        Guid dispenserId,
        string prescriptionId,
        string patientIdentifier,
        string medication,
        string dosage,
        string? correlationId = null)
    {
        return new DispenseRequest
        {
            Id = Guid.NewGuid(),
            DispenserId = dispenserId,
            PrescriptionId = prescriptionId,
            PatientIdentifier = patientIdentifier,
            Medication = medication,
            Dosage = dosage,
            Status = DispenseRequestStatus.PendingVerification,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"),
        };
    }

    public void MarkPrescriptionVerified() => Transition(DispenseRequestStatus.PendingPayment);
    public void MarkPaymentReceived(Guid paymentId)
    {
        PaymentId = paymentId;
        Transition(DispenseRequestStatus.DispatchingToMachine);
    }
    public void MarkDispatched() => Transition(DispenseRequestStatus.WaitingForMachine);
    public void MarkCompleted()
    {
        Transition(DispenseRequestStatus.Completed);
        CompletedAt = DateTimeOffset.UtcNow;
    }
    public void MarkFailed(string reason)
    {
        FailureReason = reason;
        Status = DispenseRequestStatus.Failed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void Transition(DispenseRequestStatus next)
    {
        Status = next;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
