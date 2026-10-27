using SmartDispenser.Core.Entities;

namespace SmartDispenser.Core.DTOs;

public sealed record PrescriptionVerificationResult
{
    public required string PrescriptionId { get; init; }
    public required string ThreadId { get; init; }
    public required bool IsApproved { get; init; }
    public required bool AuditChainValid { get; init; }
    public required bool SafetyApproved { get; init; }
    public required bool IsPendingApproval { get; init; }
    public string? ProposedMedication { get; init; }
    public string? Dosage { get; init; }
    public string? RiskNotes { get; init; }
    public string? DoctorComments { get; init; }

    /// <summary>Why this prescription must not be dispensed, or null when every approval gate passed.</summary>
    public string? RejectionReason => this switch
    {
        { IsPendingApproval: true } => "Prescription is pending doctor approval.",
        { IsApproved: false } => "Doctor has not approved this prescription.",
        { SafetyApproved: false } => $"Safety agent flagged an issue: {RiskNotes}",
        { AuditChainValid: false } => "Audit chain integrity check failed: possible data tampering.",
        _ => null,
    };
}

public sealed record DispenserStateDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Location { get; init; }
    public required string Status { get; init; }
    public required int InventoryCount { get; init; }
    public required int InventoryCapacity { get; init; }
    public required double InventoryPercentage { get; init; }
    public required DateTimeOffset LastHeartbeatAt { get; init; }
    public DateTimeOffset? LastDispenseAt { get; init; }
    public string? FirmwareVersion { get; init; }
    public bool IsOnline => Status != nameof(DispenserStatus.Offline);

    public static DispenserStateDto From(Dispenser d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Location = d.Location,
        Status = d.Status.ToString(),
        InventoryCount = d.InventoryCount,
        InventoryCapacity = d.InventoryCapacity,
        InventoryPercentage = Math.Round(d.InventoryPercentage, 1),
        LastHeartbeatAt = d.LastHeartbeatAt,
        LastDispenseAt = d.LastDispenseAt,
        FirmwareVersion = d.FirmwareVersion,
    };
}

public sealed record DispenseRequestDto(
    Guid Id,
    Guid DispenserId,
    string PrescriptionId,
    string Medication,
    string Dosage,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public static DispenseRequestDto From(DispenseRequest r) => new(
        r.Id, r.DispenserId, r.PrescriptionId, r.Medication, r.Dosage,
        r.Status.ToString(), r.FailureReason, r.CreatedAt, r.CompletedAt);
}

public sealed record PaymentWebhookPayload
{
    public required string TransactionId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string DispenseRequestId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public required string Provider { get; init; }
    public string? GatewayResponse { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
