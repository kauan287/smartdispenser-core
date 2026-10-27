namespace SmartDispenser.UnitTests.Domain;

public sealed class DispenserTests
{
    [Fact]
    public void New_dispenser_is_offline_with_zero_inventory()
    {
        var d = Dispenser.Create("Machine-01", "Floor 1", 50);

        d.Status.Should().Be(DispenserStatus.Offline);
        d.InventoryCount.Should().Be(0);
        d.CanDispense().Should().BeFalse();
    }

    [Fact]
    public void Online_dispenser_with_inventory_can_dispense()
    {
        var d = Dispenser.Create("Machine-01", "Floor 1", 50);
        d.GoOnline();
        d.UpdateInventory(10);

        d.CanDispense().Should().BeTrue();
    }

    [Fact]
    public void Dispensing_dispenser_cannot_begin_another_dispense()
    {
        var d = Dispenser.Create("Machine-01", "Floor 1", 50);
        d.GoOnline();
        d.UpdateInventory(10);
        d.BeginDispensing(Guid.NewGuid());

        d.Status.Should().Be(DispenserStatus.Dispensing);
        d.CanDispense().Should().BeFalse();
        var act = () => d.BeginDispensing(Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>().WithMessage("*'Dispensing'*");
    }

    [Fact]
    public void Completing_a_dispense_decrements_inventory_and_returns_to_idle()
    {
        var d = Dispenser.Create("Machine-01", "Floor 1", 50);
        d.GoOnline();
        d.UpdateInventory(10);
        d.BeginDispensing(Guid.NewGuid());
        d.CompleteDispensing();

        d.InventoryCount.Should().Be(9);
        d.Status.Should().Be(DispenserStatus.Idle);
    }

    [Fact]
    public void Offline_dispenser_cannot_begin_dispensing()
    {
        var d = Dispenser.Create("Machine-01", "Floor 1", 50);

        var act = () => d.BeginDispensing(Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot begin dispensing: dispenser 'Machine-01' is in state 'Offline' (expected 'Idle').");
    }
}
