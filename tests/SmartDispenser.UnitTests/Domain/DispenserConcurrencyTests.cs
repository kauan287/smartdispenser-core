namespace SmartDispenser.UnitTests.Domain;

public sealed class DispenserConcurrencyTests
{
    [Fact]
    public async Task Two_requests_racing_for_the_same_idle_machine_cannot_both_dispatch()
    {
        var dbName = Guid.NewGuid().ToString();
        var dispenser = Dispenser.Create("Machine-01", "Pharmacy A", 10);
        dispenser.GoOnline();
        dispenser.UpdateInventory(5);
        await using (var seed = TestDb.Create(dbName))
        {
            seed.Dispensers.Add(dispenser);
            await seed.SaveChangesAsync();
        }

        await using var first = TestDb.Create(dbName);
        await using var second = TestDb.Create(dbName);
        var seenByFirst = await first.Dispensers.SingleAsync();
        var seenBySecond = await second.Dispensers.SingleAsync();
        var firstRequest = Guid.NewGuid();
        seenByFirst.BeginDispensing(firstRequest);
        seenBySecond.BeginDispensing(Guid.NewGuid());

        await first.SaveChangesAsync();
        var act = () => second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using var after = TestDb.Create(dbName);
        (await after.Dispensers.SingleAsync()).CurrentDispenseRequestId.Should().Be(firstRequest);
    }

    [Fact]
    public async Task Heartbeat_does_not_conflict_with_a_concurrent_dispatch()
    {
        var dbName = Guid.NewGuid().ToString();
        var dispenser = Dispenser.Create("Machine-01", "Pharmacy A", 10);
        dispenser.GoOnline();
        dispenser.UpdateInventory(5);
        await using (var seed = TestDb.Create(dbName))
        {
            seed.Dispensers.Add(dispenser);
            await seed.SaveChangesAsync();
        }

        await using var payment = TestDb.Create(dbName);
        await using var heartbeat = TestDb.Create(dbName);
        var dispatching = await payment.Dispensers.SingleAsync();
        var beating = await heartbeat.Dispensers.SingleAsync();
        dispatching.BeginDispensing(Guid.NewGuid());
        beating.RecordHeartbeat();

        await payment.SaveChangesAsync();
        await heartbeat.SaveChangesAsync();

        await using var after = TestDb.Create(dbName);
        (await after.Dispensers.SingleAsync()).Status.Should().Be(DispenserStatus.Dispensing);
    }
}
