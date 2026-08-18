namespace SmartDispenser.Core.DTOs;

/// <summary>
/// Result of verifying a prescription against the HealthTech Agent Orchestrator API.
/// Maps from the Python API response shape.
/// </summary>
public sealed record PrescriptionVerificationResult
{
    public required string PrescriptionId { get; init; }
    public required string ThreadId { get; init; }

    /// <summary>True when a doctor has explicitly approved the prescription (HITL step passed).</summary>
    public required bool IsApproved { get; init; }

    /// <summary>True when the SHA-256 audit chain is intact (no tampering detected).</summary>
    public required bool AuditChainValid { get; init; }

    /// <summary>True when the AI safety agent approved the medication (allergy check passed).</summary>
    public required bool SafetyApproved { get; init; }

    /// <summary>True when the prescription is awaiting doctor approval (HITL not completed yet).</summary>
    public required bool IsPendingApproval { get; init; }

    public string? ProposedMedication { get; init; }
    public string? Dosage { get; init; }
    public string? RiskNotes { get; init; }
    public string? DoctorComments { get; init; }

    /// <summary>
    /// Returns true only when ALL conditions for safe dispensing are met:
    /// - Doctor has approved
    /// - AI safety agent approved (no allergy conflict)
    /// - Cryptographic audit chain is intact
    /// </summary>
    public bool IsReadyForDispensing =>
        IsApproved && SafetyApproved && AuditChainValid && !IsPendingApproval;

    public string GetRejectionReason()
    {
        if (IsPendingApproval) return "Prescription is pending doctor approval.";
        if (!IsApproved) return "Doctor has not approved this prescription.";
        if (!SafetyApproved) return $"Safety agent flagged an issue: {RiskNotes}";
        if (!AuditChainValid) return "⚠️ Audit chain integrity check FAILED — possible data tampering.";
        return "Unknown rejection reason.";
    }
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
    public bool IsOnline => Status != "Offline";
}

public sealed record PaymentWebhookPayload
{
    public required string TransactionId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string DispenseRequestId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }    // "confirmed" | "failed"
    public required string Provider { get; init; }  // "redsys" | "stripe" | "sandbox"
    public string? GatewayResponse { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
