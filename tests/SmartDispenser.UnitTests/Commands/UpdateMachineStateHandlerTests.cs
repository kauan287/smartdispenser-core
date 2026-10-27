using SmartDispenser.Core.Commands.UpdateMachineState;
using SmartDispenser.Infrastructure.Repositories;

namespace SmartDispenser.UnitTests.Commands;

public sealed class UpdateMachineStateHandlerTests
{
    private readonly SmartDispenser.Infrastructure.Data.AppDbContext _db = TestDb.Create();

    [Fact]
    public async Task Confirmation_for_another_request_leaves_the_current_dispense_running()
    {
        var (dispenser, request) = await SeedDispensing(inventory: 5);

        var act = () => Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.DispenseCompleted,
            CompletedRequestId: Guid.NewGuid()));

        await act.Should().ThrowAsync<InvalidOperationException>();
        dispenser.Status.Should().Be(DispenserStatus.Dispensing);
        dispenser.InventoryCount.Should().Be(5);
        dispenser.CurrentDispenseRequestId.Should().Be(request.Id);
    }

    [Fact]
    public async Task Machine_going_offline_mid_dispense_fails_the_request()
    {
        var (dispenser, request) = await SeedDispensing(inventory: 5);

        await Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.Offline));

        dispenser.Status.Should().Be(DispenserStatus.Offline);
        request.Status.Should().Be(DispenseRequestStatus.Failed);
        request.FailureReason.Should().Be("Machine went offline during dispense.");
    }

    private Task Handle(UpdateMachineStateCommand cmd) =>
        new UpdateMachineStateHandler(
            new DispenserRepository(_db),
            new DispenseRequestRepository(_db),
            new Mock<IDispenserHubContext>().Object,
            NullLogger<UpdateMachineStateHandler>.Instance).Handle(cmd, CancellationToken.None);

    private async Task<(Dispenser, DispenseRequest)> SeedDispensing(int inventory)
    {
        var dispenser = Dispenser.Create("Machine-01", "Floor 1", 10);
        dispenser.GoOnline();
        dispenser.UpdateInventory(inventory);
        var request = DispenseRequest.Create(dispenser.Id, "RX-1", "PATIENT-1", "Amoxicillin", "500 mg");
        request.MarkPrescriptionVerified();
        request.MarkPaymentReceived(Guid.NewGuid());
        request.MarkDispatched();
        dispenser.BeginDispensing(request.Id);
        _db.AddRange(dispenser, request);
        await _db.SaveChangesAsync();
        return (dispenser, request);
    }
}
