using SmartDispenser.Core.Commands.UpdateMachineState;
using SmartDispenser.Infrastructure.Repositories;

namespace SmartDispenser.UnitTests.Commands;

public sealed class UpdateMachineStateHandlerTests
{
    private readonly SmartDispenser.Infrastructure.Data.AppDbContext _db = TestDb.Create();
    private readonly RecordingNotifier _notifier = new();

    [Fact]
    public async Task Confirmation_for_another_request_leaves_the_current_dispense_running()
    {
        var (dispenser, request) = await SeedDispensing(inventory: 5);

        var act = () => Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.DispenseCompleted,
            RequestId: Guid.NewGuid()));

        await act.Should().ThrowAsync<InvalidOperationException>();
        dispenser.Status.Should().Be(DispenserStatus.Dispensing);
        dispenser.InventoryCount.Should().Be(5);
        dispenser.CurrentDispenseRequestId.Should().Be(request.Id);
    }

    [Fact]
    public async Task Heartbeat_from_a_machine_that_is_already_online_publishes_nothing()
    {
        var dispenser = Dispenser.Create("Machine-01", "Floor 1", 10);
        dispenser.GoOnline();
        dispenser.UpdateInventory(5);
        _db.Dispensers.Add(dispenser);
        await _db.SaveChangesAsync();

        await Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.Heartbeat));

        _notifier.States.Should().BeEmpty();
    }

    [Fact]
    public async Task Heartbeat_from_an_offline_machine_brings_it_back_and_publishes_its_state()
    {
        var dispenser = Dispenser.Create("Machine-01", "Floor 1", 10);
        _db.Dispensers.Add(dispenser);
        await _db.SaveChangesAsync();

        await Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.Heartbeat));

        _notifier.States.Select(s => s.Status).Should().Equal("InventoryEmpty");
    }

    [Fact]
    public async Task Heartbeat_does_not_make_a_concurrent_dispatch_lose_its_save()
    {
        var dbName = Guid.NewGuid().ToString();
        var dispenser = Dispenser.Create("Machine-01", "Floor 1", 10);
        dispenser.GoOnline();
        dispenser.UpdateInventory(5);
        await using (var seed = TestDb.Create(dbName))
        {
            seed.Dispensers.Add(dispenser);
            await seed.SaveChangesAsync();
        }
        await using var webhook = TestDb.Create(dbName);
        var seenByWebhook = await webhook.Dispensers.SingleAsync();
        await using var hub = TestDb.Create(dbName);
        await new UpdateMachineStateHandler(new DispenserRepository(hub), new DispenseRequestRepository(hub), _notifier, hub,
                NullLogger<UpdateMachineStateHandler>.Instance)
            .Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.Heartbeat), CancellationToken.None);
        var requestId = Guid.NewGuid();

        seenByWebhook.BeginDispensing(requestId);
        await webhook.SaveChangesAsync();

        await using var after = TestDb.Create(dbName);
        var saved = await after.Dispensers.SingleAsync();
        saved.Status.Should().Be(DispenserStatus.Dispensing);
        saved.CurrentDispenseRequestId.Should().Be(requestId);
    }

    [Fact]
    public async Task Machine_going_offline_mid_dispense_interrupts_the_request()
    {
        var (dispenser, request) = await SeedDispensing(inventory: 5);

        await Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.Offline));

        dispenser.Status.Should().Be(DispenserStatus.Offline);
        dispenser.CurrentDispenseRequestId.Should().BeNull();
        request.Status.Should().Be(DispenseRequestStatus.Interrupted);
        request.FailureReason.Should().BeNull();
        (await _db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Expired_dispatch_interrupts_the_request_and_moves_the_machine_to_error()
    {
        var (dispenser, request) = await SeedDispensing(inventory: 5);

        await Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.DispatchExpired, RequestId: request.Id));

        dispenser.Status.Should().Be(DispenserStatus.Error);
        dispenser.CurrentDispenseRequestId.Should().BeNull();
        dispenser.InventoryCount.Should().Be(5);
        request.Status.Should().Be(DispenseRequestStatus.Interrupted);
    }

    [Fact]
    public async Task Late_confirmation_of_an_interrupted_dispense_completes_it_while_another_one_runs()
    {
        var (dispenser, interrupted) = await SeedDispensing(inventory: 5);
        await Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.Offline));
        await Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.Online));
        var current = await Dispatch(dispenser, "RX-2");

        await Handle(new UpdateMachineStateCommand(dispenser.Id, MachineStateEvent.DispenseCompleted, RequestId: interrupted.Id));

        interrupted.Status.Should().Be(DispenseRequestStatus.Completed);
        dispenser.Status.Should().Be(DispenserStatus.Dispensing);
        dispenser.CurrentDispenseRequestId.Should().Be(current.Id);
        dispenser.InventoryCount.Should().Be(4);
        (await _db.OutboxMessages.SingleAsync()).Deserialize<DispenseResultNotification>()
            .Should().Be(new DispenseResultNotification(interrupted.Id, true, null));
    }

    [Fact]
    public async Task Machine_cannot_resolve_a_request_dispatched_to_another_machine()
    {
        var (owner, request) = await SeedDispensing(inventory: 5);
        await Handle(new UpdateMachineStateCommand(owner.Id, MachineStateEvent.Offline));
        var other = Dispenser.Create("Machine-02", "Floor 2", 10);
        other.GoOnline();
        _db.Dispensers.Add(other);
        await _db.SaveChangesAsync();

        var act = () => Handle(new UpdateMachineStateCommand(other.Id, MachineStateEvent.DispenseFailed, RequestId: request.Id));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Dispense request {request.Id} does not belong to dispenser 'Machine-02'.");
        request.Status.Should().Be(DispenseRequestStatus.Interrupted);
    }

    private Task Handle(UpdateMachineStateCommand cmd) =>
        new UpdateMachineStateHandler(
            new DispenserRepository(_db),
            new DispenseRequestRepository(_db),
            _notifier,
            _db,
            NullLogger<UpdateMachineStateHandler>.Instance).Handle(cmd, CancellationToken.None);

    private async Task<DispenseRequest> Dispatch(Dispenser dispenser, string prescriptionId)
    {
        var request = DispenseRequest.Create(dispenser.Id, prescriptionId, "PATIENT-1", "Amoxicillin", "500 mg");
        request.RecordPayment(Guid.NewGuid());
        request.MarkDispatched();
        dispenser.BeginDispensing(request.Id);
        _db.Add(request);
        await _db.SaveChangesAsync();
        return request;
    }

    private async Task<(Dispenser, DispenseRequest)> SeedDispensing(int inventory)
    {
        var dispenser = Dispenser.Create("Machine-01", "Floor 1", 10);
        dispenser.GoOnline();
        dispenser.UpdateInventory(inventory);
        var request = DispenseRequest.Create(dispenser.Id, "RX-1", "PATIENT-1", "Amoxicillin", "500 mg");
        request.RecordPayment(Guid.NewGuid());
        request.MarkDispatched();
        dispenser.BeginDispensing(request.Id);
        _db.AddRange(dispenser, request);
        await _db.SaveChangesAsync();
        return (dispenser, request);
    }
}
