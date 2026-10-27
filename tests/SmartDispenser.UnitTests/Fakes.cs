namespace SmartDispenser.UnitTests;

public sealed class FakeHealthTechClient : IHealthTechApiClient
{
    private readonly Dictionary<string, PrescriptionVerificationResult> _prescriptions = new();
    public Exception? Failure { get; set; }

    public void Add(PrescriptionVerificationResult prescription) => _prescriptions[prescription.PrescriptionId] = prescription;

    public Task<PrescriptionVerificationResult?> VerifyPrescriptionAsync(string prescriptionId, CancellationToken ct = default) =>
        Failure is not null
            ? Task.FromException<PrescriptionVerificationResult?>(Failure)
            : Task.FromResult(_prescriptions.GetValueOrDefault(prescriptionId));

    public static PrescriptionVerificationResult Approved(string id) => new()
    {
        PrescriptionId = id,
        ThreadId = $"session-{id}",
        IsApproved = true,
        SafetyApproved = true,
        AuditChainValid = true,
        IsPendingApproval = false,
        ProposedMedication = "Amoxicillin",
        Dosage = "500 mg every 8 hours",
    };
}

public sealed class RecordingNotifier : IDispenserNotifier
{
    public List<(Guid DispenserId, DispenseCommand Command)> Commands { get; } = [];
    public List<DispenserStateDto> States { get; } = [];

    public Task SendDispenseCommandAsync(Guid dispenserId, DispenseCommand command, CancellationToken ct = default)
    {
        Commands.Add((dispenserId, command));
        return Task.CompletedTask;
    }

    public Task PublishStateAsync(DispenserStateDto state, CancellationToken ct = default)
    {
        States.Add(state);
        return Task.CompletedTask;
    }

    public Task PublishDispenseResultAsync(DispenseResultNotification result, CancellationToken ct = default) => Task.CompletedTask;
}
