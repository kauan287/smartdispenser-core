namespace SmartDispenser.UnitTests.Domain;

public sealed class OutboxTests
{
    [Fact]
    public void Failed_delivery_backs_off_exponentially_then_dead_letters_on_the_fifth_failure()
    {
        var message = OutboxMessage.Create(OutboxEvents.DispenseResult, new DispenseResultNotification(Guid.Empty, true));
        var delays = new List<int>();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var before = DateTimeOffset.UtcNow;
            message.RecordFailure("timeout");
            delays.Add((int)Math.Round((message.NextRetryAt!.Value - before).TotalSeconds));
        }
        message.Status.Should().Be(OutboxMessageStatus.Pending);

        message.RecordFailure("timeout");

        delays.Should().Equal(5, 25, 125, 625);
        message.Status.Should().Be(OutboxMessageStatus.DeadLetter);
        message.RetryCount.Should().Be(5);
        message.LastError.Should().Be("timeout");
    }

    [Fact]
    public async Task Finishing_a_request_writes_its_result_to_the_outbox_in_the_same_save()
    {
        await using var db = TestDb.Create();
        var request = DispenseRequest.Create(Guid.NewGuid(), "RX-1", "PATIENT-1", "Amoxicillin", "500 mg");
        db.DispenseRequests.Add(request);
        await db.SaveChangesAsync();
        (await db.OutboxMessages.CountAsync()).Should().Be(0);

        request.MarkFailed("Payment failed: card declined");
        await db.SaveChangesAsync();

        var message = await db.OutboxMessages.SingleAsync();
        message.EventType.Should().Be("DispenseResult");
        message.Status.Should().Be(OutboxMessageStatus.Pending);
        message.Deserialize<DispenseResultNotification>()
            .Should().Be(new DispenseResultNotification(request.Id, false, "Payment failed: card declined"));
    }

    [Fact]
    public async Task Saving_an_unchanged_finished_request_does_not_write_a_second_message()
    {
        await using var db = TestDb.Create();
        var request = DispenseRequest.Create(Guid.NewGuid(), "RX-1", "PATIENT-1", "Amoxicillin", "500 mg");
        db.DispenseRequests.Add(request);
        await db.SaveChangesAsync();
        request.MarkFailed("Payment failed");
        await db.SaveChangesAsync();

        db.DispenseRequests.Update(request);
        await db.SaveChangesAsync();

        (await db.OutboxMessages.CountAsync()).Should().Be(1);
    }
}
