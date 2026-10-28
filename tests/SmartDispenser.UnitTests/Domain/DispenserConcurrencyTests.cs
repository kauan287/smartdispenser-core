namespace SmartDispenser.UnitTests.Domain;

public sealed class DispenserConcurrencyTests
{
    [Fact]
    public async Task Two_requests_racing_for_the_same_idle_machine_cannot_both_dispatch()
    {
        var dbName = Guid.NewGuid().ToString();
        var dispenser = Dispenser.Create("Machine-01", "Pharmacy A", "Amoxicillin", 10);
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
}
