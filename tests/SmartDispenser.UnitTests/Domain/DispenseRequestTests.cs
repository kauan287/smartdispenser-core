namespace SmartDispenser.UnitTests.Domain;

public sealed class DispenseRequestTests
{
    private static DispenseRequest NewRequest() =>
        DispenseRequest.Create(Guid.NewGuid(), "RX-1", "PATIENT-1", "Amoxicillin", "500 mg");

    [Fact]
    public void Paid_request_moves_through_dispatched_to_completed()
    {
        var request = NewRequest();
        var paymentId = Guid.NewGuid();

        request.RecordPayment(paymentId);
        request.MarkDispatched();
        request.MarkCompleted();

        request.Status.Should().Be(DispenseRequestStatus.Completed);
        request.PaymentId.Should().Be(paymentId);
        request.CompletedAt.Should().Be(request.UpdatedAt);
    }

    [Fact]
    public void Unpaid_request_cannot_be_dispatched()
    {
        var request = NewRequest();

        var act = () => request.MarkDispatched();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Dispense request {request.Id} cannot be dispatched before it is paid.");
        request.Status.Should().Be(DispenseRequestStatus.PendingPayment);
    }

    [Fact]
    public void Dispatched_request_cannot_be_paid_again()
    {
        var request = NewRequest();
        request.RecordPayment(Guid.NewGuid());
        request.MarkDispatched();

        var act = () => request.RecordPayment(Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Dispense request {request.Id} is not awaiting payment (status: Dispatched).");
    }

    [Fact]
    public void Completed_request_cannot_fail_afterwards()
    {
        var request = NewRequest();
        request.RecordPayment(Guid.NewGuid());
        request.MarkDispatched();
        request.MarkCompleted();

        var act = () => request.MarkFailed("late error");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Dispense request {request.Id} cannot move from Completed to Failed.");
        request.FailureReason.Should().BeNull();
    }

    [Fact]
    public void Pending_request_cannot_complete_without_being_dispatched()
    {
        var request = NewRequest();

        var act = () => request.MarkCompleted();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Dispense request {request.Id} cannot move from PendingPayment to Completed.");
    }
}
