namespace SmartDispenser.Core.Entities;

/// <summary>
/// One attempt to dispense one verified prescription:
/// PendingPayment → Dispatched → Completed, or Failed from either non-terminal state.
/// </summary>
public sealed class DispenseRequest
{
    private static readonly Dictionary<DispenseRequestStatus, DispenseRequestStatus[]> AllowedTransitions = new()
    {
        [DispenseRequestStatus.PendingPayment] = [DispenseRequestStatus.Dispatched, DispenseRequestStatus.Failed],
        [DispenseRequestStatus.Dispatched] = [DispenseRequestStatus.Completed, DispenseRequestStatus.Failed],
        [DispenseRequestStatus.Completed] = [],
        [DispenseRequestStatus.Failed] = [],
    };

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

    private DispenseRequest() { }

    public static DispenseRequest Create(
        Guid dispenserId,
        string prescriptionId,
        string patientIdentifier,
        string medication,
        string dosage)
    {
        var now = DateTimeOffset.UtcNow;
        return new DispenseRequest
        {
            Id = Guid.NewGuid(),
            DispenserId = dispenserId,
            PrescriptionId = prescriptionId,
            PatientIdentifier = patientIdentifier,
            Medication = medication,
            Dosage = dosage,
            Status = DispenseRequestStatus.PendingPayment,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void RecordPayment(Guid paymentId)
    {
        if (Status != DispenseRequestStatus.PendingPayment)
            throw new InvalidOperationException($"Dispense request {Id} is not awaiting payment (status: {Status}).");
        PaymentId = paymentId;
    }

    public void MarkDispatched()
    {
        if (PaymentId is null)
            throw new InvalidOperationException($"Dispense request {Id} cannot be dispatched before it is paid.");
        TransitionTo(DispenseRequestStatus.Dispatched);
    }

    public void MarkCompleted()
    {
        TransitionTo(DispenseRequestStatus.Completed);
        CompletedAt = UpdatedAt;
    }

    public void MarkFailed(string reason)
    {
        TransitionTo(DispenseRequestStatus.Failed);
        FailureReason = reason;
    }

    private void TransitionTo(DispenseRequestStatus next)
    {
        if (!AllowedTransitions[Status].Contains(next))
            throw new InvalidOperationException($"Dispense request {Id} cannot move from {Status} to {next}.");
        Status = next;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
