using SmartDispenser.Core.Commands.InitiateDispense;
using SmartDispenser.Infrastructure.Data;
using SmartDispenser.Infrastructure.Repositories;

namespace SmartDispenser.UnitTests.Commands;

public sealed class InitiateDispenseHandlerTests
{
    private readonly AppDbContext _db = TestDb.Create();
    private readonly FakeHealthTechClient _healthTech = new();

    [Fact]
    public async Task Approved_prescription_on_an_idle_machine_is_reserved_pending_payment()
    {
        var dispenser = await SeedIdleDispenser();
        _healthTech.Add(FakeHealthTechClient.Approved("RX-001"));

        var result = await Handle(new InitiateDispenseCommand(dispenser.Id, "RX-001", "PATIENT-42"));

        result.Error.Should().BeNull();
        var request = await _db.DispenseRequests.SingleAsync();
        request.Id.Should().Be(result.DispenseRequestId!.Value);
        request.Status.Should().Be(DispenseRequestStatus.PendingPayment);
        request.Medication.Should().Be("Amoxicillin");
        request.Dosage.Should().Be("500 mg every 8 hours");
        dispenser.Status.Should().Be(DispenserStatus.Idle);
    }

    [Theory]
    [InlineData("pending", "Prescription is pending doctor approval.")]
    [InlineData("unapproved", "Doctor has not approved this prescription.")]
    [InlineData("unsafe", "Safety agent flagged an issue: Penicillin allergy")]
    [InlineData("tampered", "Audit chain integrity check failed: possible data tampering.")]
    [InlineData("no-dosage", "Prescription has no medication or dosage.")]
    public async Task Prescription_failing_a_gate_is_rejected_and_nothing_is_reserved(string variant, string expectedReason)
    {
        var dispenser = await SeedIdleDispenser();
        var approved = FakeHealthTechClient.Approved("RX-002");
        _healthTech.Add(variant switch
        {
            "pending" => approved with { IsApproved = false, IsPendingApproval = true },
            "unapproved" => approved with { IsApproved = false },
            "unsafe" => approved with { SafetyApproved = false, RiskNotes = "Penicillin allergy" },
            "tampered" => approved with { AuditChainValid = false },
            "no-dosage" => approved with { Dosage = null },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        });

        var result = await Handle(new InitiateDispenseCommand(dispenser.Id, "RX-002", "PATIENT-42"));

        result.Error.Should().Be(InitiateDispenseError.Rejected);
        result.ErrorMessage.Should().Be(expectedReason);
        (await _db.DispenseRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Unknown_prescription_is_rejected_as_not_found()
    {
        var dispenser = await SeedIdleDispenser();

        var result = await Handle(new InitiateDispenseCommand(dispenser.Id, "RX-404", "PATIENT-42"));

        result.Error.Should().Be(InitiateDispenseError.Rejected);
        result.ErrorMessage.Should().Be("Prescription 'RX-404' was not found in the HealthTech Orchestrator.");
    }

    [Fact]
    public async Task Orchestrator_outage_is_reported_as_unavailable()
    {
        var dispenser = await SeedIdleDispenser();
        _healthTech.Failure = new HttpRequestException("Circuit breaker open");

        var result = await Handle(new InitiateDispenseCommand(dispenser.Id, "RX-003", "PATIENT-42"));

        result.Error.Should().Be(InitiateDispenseError.VerificationUnavailable);
        result.ErrorMessage.Should().Be("Prescription verification service unavailable. Please try again later.");
    }

    [Fact]
    public async Task Offline_machine_is_rejected()
    {
        var dispenser = await SeedIdleDispenser();
        dispenser.GoOffline();
        await _db.SaveChangesAsync();
        _healthTech.Add(FakeHealthTechClient.Approved("RX-004"));

        var result = await Handle(new InitiateDispenseCommand(dispenser.Id, "RX-004", "PATIENT-42"));

        result.Error.Should().Be(InitiateDispenseError.Rejected);
        result.ErrorMessage.Should().Be("Dispenser 'Machine-01' is not ready (status: Offline).");
    }

    [Fact]
    public async Task Unknown_dispenser_is_reported_as_not_found()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var result = await Handle(new InitiateDispenseCommand(id, "RX-005", "PATIENT-42"));

        result.Error.Should().Be(InitiateDispenseError.DispenserNotFound);
        result.ErrorMessage.Should().Be("Dispenser 00000000-0000-0000-0000-000000000001 not found.");
    }

    [Fact]
    public async Task Prescription_whose_earlier_request_failed_can_be_dispensed_again()
    {
        var dispenser = await SeedIdleDispenser();
        _healthTech.Add(FakeHealthTechClient.Approved("RX-006"));
        var first = await Handle(new InitiateDispenseCommand(dispenser.Id, "RX-006", "PATIENT-42"));
        var firstRequest = await _db.DispenseRequests.SingleAsync();
        firstRequest.MarkFailed("Payment failed: card declined");
        await _db.SaveChangesAsync();

        var second = await Handle(new InitiateDispenseCommand(dispenser.Id, "RX-006", "PATIENT-42"));
        var third = await Handle(new InitiateDispenseCommand(dispenser.Id, "RX-006", "PATIENT-42"));

        first.Error.Should().BeNull();
        second.Error.Should().BeNull();
        third.ErrorMessage.Should().Be("Prescription 'RX-006' already has a dispense request.");
    }

    private Task<InitiateDispenseResult> Handle(InitiateDispenseCommand cmd) =>
        new InitiateDispenseHandler(_healthTech, new DispenserRepository(_db), new DispenseRequestRepository(_db), _db,
            NullLogger<InitiateDispenseHandler>.Instance).Handle(cmd, CancellationToken.None);

    private async Task<Dispenser> SeedIdleDispenser()
    {
        var d = Dispenser.Create("Machine-01", "Pharmacy A", inventoryCapacity: 50);
        d.GoOnline();
        d.UpdateInventory(30);
        _db.Dispensers.Add(d);
        await _db.SaveChangesAsync();
        return d;
    }
}
