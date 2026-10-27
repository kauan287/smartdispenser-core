using SmartDispenser.Core.Commands.ProcessPaymentWebhook;
using SmartDispenser.Infrastructure.Data;
using SmartDispenser.Infrastructure.Repositories;

namespace SmartDispenser.UnitTests.Commands;

public sealed class ProcessPaymentWebhookHandlerTests
{
    private readonly AppDbContext _db = TestDb.Create();
    private readonly RecordingNotifier _notifier = new();

    [Fact]
    public async Task Confirmed_payment_dispatches_the_reserved_request_to_its_machine()
    {
        var (dispenser, request) = await SeedPendingRequest();

        var result = await Handle(Payment(request.Id, "TX-1", "confirmed"));

        result.Success.Should().BeTrue();
        request.Status.Should().Be(DispenseRequestStatus.Dispatched);
        dispenser.Status.Should().Be(DispenserStatus.Dispensing);
        dispenser.CurrentDispenseRequestId.Should().Be(request.Id);
        _notifier.Commands.Should().Equal(
            (dispenser.Id, new DispenseCommand(request.Id, "RX-1", "Amoxicillin", "500 mg every 8 hours")));
        var payment = await _db.Payments.SingleAsync();
        payment.Status.Should().Be(PaymentStatus.Confirmed);
        request.PaymentId.Should().Be(payment.Id);
    }

    [Fact]
    public async Task Second_payment_for_a_dispatched_request_does_not_dispense_again()
    {
        var (_, request) = await SeedPendingRequest();
        await Handle(Payment(request.Id, "TX-1", "confirmed"));

        var second = await Handle(Payment(request.Id, "TX-2", "confirmed"));

        second.Success.Should().BeFalse();
        second.ErrorMessage.Should().Be($"DispenseRequest {request.Id} is not awaiting payment (status: Dispatched).");
        _notifier.Commands.Should().HaveCount(1);
    }

    [Fact]
    public async Task Failed_payment_fails_the_request_without_dispensing()
    {
        var (dispenser, request) = await SeedPendingRequest();

        await Handle(Payment(request.Id, "TX-1", "failed") with { GatewayResponse = "card declined" });

        request.Status.Should().Be(DispenseRequestStatus.Failed);
        request.FailureReason.Should().Be("Payment failed: card declined");
        dispenser.Status.Should().Be(DispenserStatus.Idle);
        _notifier.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Payment_arriving_after_the_machine_went_offline_fails_the_request_for_refund()
    {
        var (dispenser, request) = await SeedPendingRequest();
        dispenser.GoOffline();
        await _db.SaveChangesAsync();

        await Handle(Payment(request.Id, "TX-1", "confirmed"));

        var payment = await _db.Payments.SingleAsync();
        request.Status.Should().Be(DispenseRequestStatus.Failed);
        request.FailureReason.Should().Be(
            $"Dispenser was not ready when payment {payment.Id} arrived (status: Offline). Refund required.");
        payment.Status.Should().Be(PaymentStatus.Confirmed);
        _notifier.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Payment_and_dispatch_are_saved_together_or_not_at_all()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid requestId;
        await using (var seed = TestDb.Create(dbName))
        {
            var dispenser = Dispenser.Create("Machine-01", "Pharmacy A", 10);
            dispenser.GoOnline();
            dispenser.UpdateInventory(5);
            var request = DispenseRequest.Create(dispenser.Id, "RX-1", "PATIENT-1", "Amoxicillin", "500 mg");
            seed.AddRange(dispenser, request);
            await seed.SaveChangesAsync();
            requestId = request.Id;
        }

        await using (var failing = TestDb.Create(dbName, new FailDispenserWrites()))
        {
            var handler = new ProcessPaymentWebhookHandler(new PaymentRepository(failing), new DispenseRequestRepository(failing),
                new DispenserRepository(failing), _notifier, failing, NullLogger<ProcessPaymentWebhookHandler>.Instance);
            var act = () => handler.Handle(new ProcessPaymentWebhookCommand(Payment(requestId, "TX-1", "confirmed")), CancellationToken.None);
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using var after = TestDb.Create(dbName);
        (await after.Payments.CountAsync()).Should().Be(0, "a retried webhook must not be treated as a duplicate");
        (await after.DispenseRequests.SingleAsync()).Status.Should().Be(DispenseRequestStatus.PendingPayment);
        _notifier.Commands.Should().BeEmpty();
    }

    private Task<ProcessPaymentResult> Handle(PaymentWebhookPayload payload) =>
        new ProcessPaymentWebhookHandler(new PaymentRepository(_db), new DispenseRequestRepository(_db),
            new DispenserRepository(_db), _notifier, _db, NullLogger<ProcessPaymentWebhookHandler>.Instance)
            .Handle(new ProcessPaymentWebhookCommand(payload), CancellationToken.None);

    private static PaymentWebhookPayload Payment(Guid requestId, string transactionId, string status) => new()
    {
        TransactionId = transactionId,
        IdempotencyKey = $"idem-{transactionId}",
        DispenseRequestId = requestId.ToString(),
        Amount = 12.50m,
        Currency = "EUR",
        Status = status,
        Provider = "sandbox",
        Timestamp = DateTimeOffset.Parse("2026-10-27T12:00:00Z"),
    };

    private async Task<(Dispenser, DispenseRequest)> SeedPendingRequest()
    {
        var dispenser = Dispenser.Create("Machine-01", "Pharmacy A", 10);
        dispenser.GoOnline();
        dispenser.UpdateInventory(5);
        var request = DispenseRequest.Create(dispenser.Id, "RX-1", "PATIENT-1", "Amoxicillin", "500 mg every 8 hours");
        _db.AddRange(dispenser, request);
        await _db.SaveChangesAsync();
        return (dispenser, request);
    }
}
