namespace SmartDispenser.Core.Entities;

/// <summary>
/// Domain entity representing a physical smart vending machine.
/// Models an explicit state machine: state transitions are validated
/// to prevent invalid operations (e.g. dispensing while offline).
/// </summary>
public sealed class Dispenser
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Location { get; private set; } = string.Empty;
    public DispenserStatus Status { get; private set; }
    public int InventoryCount { get; private set; }
    public int InventoryCapacity { get; private set; }
    public DateTimeOffset LastHeartbeatAt { get; private set; }
    public DateTimeOffset? LastDispenseAt { get; private set; }
    public Guid? CurrentDispenseRequestId { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // EF Core constructor
    private Dispenser() { }

    public static Dispenser Create(string name, string location, int inventoryCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inventoryCapacity);

        return new Dispenser
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = location,
            Status = DispenserStatus.Offline,
            InventoryCount = 0,
            InventoryCapacity = inventoryCapacity,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ── State machine transitions ──────────────────────────────────────────────

    /// <summary>Returns true if the dispenser is in a state that allows dispensing.</summary>
    public bool CanDispense() =>
        Status == DispenserStatus.Idle && InventoryCount > 0;

    public void BeginDispensing(Guid requestId)
    {
        EnsureStatus(DispenserStatus.Idle, "begin dispensing");
        EnsureInventoryAvailable();

        Status = DispenserStatus.Dispensing;
        CurrentDispenseRequestId = requestId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void CompleteDispensing()
    {
        EnsureStatus(DispenserStatus.Dispensing, "complete dispensing");

        Status = DispenserStatus.Idle;
        InventoryCount = Math.Max(0, InventoryCount - 1);
        LastDispenseAt = DateTimeOffset.UtcNow;
        CurrentDispenseRequestId = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void FailDispensing(string reason)
    {
        if (Status != DispenserStatus.Dispensing)
            return; // Idempotent — already recovered

        Status = DispenserStatus.Error;
        CurrentDispenseRequestId = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void GoOnline(string? firmwareVersion = null)
    {
        Status = DispenserStatus.Idle;
        FirmwareVersion = firmwareVersion ?? FirmwareVersion;
        LastHeartbeatAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void GoOffline()
    {
        if (Status == DispenserStatus.Dispensing)
            FailDispensing("Machine went offline during dispense");

        Status = DispenserStatus.Offline;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordHeartbeat()
    {
        LastHeartbeatAt = DateTimeOffset.UtcNow;
        if (Status == DispenserStatus.Offline)
            Status = DispenserStatus.Idle;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void UpdateInventory(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        InventoryCount = Math.Min(count, InventoryCapacity);
        if (InventoryCount == 0 && Status == DispenserStatus.Idle)
            Status = DispenserStatus.InventoryEmpty;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ResetFromError()
    {
        EnsureStatus(DispenserStatus.Error, "reset");
        Status = DispenserStatus.Idle;
        CurrentDispenseRequestId = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public double InventoryPercentage =>
        InventoryCapacity == 0 ? 0 : (double)InventoryCount / InventoryCapacity * 100;

    public bool IsStale(TimeSpan threshold) =>
        DateTimeOffset.UtcNow - LastHeartbeatAt > threshold;

    // ── Guards ────────────────────────────────────────────────────────────────

    private void EnsureStatus(DispenserStatus expected, string operation)
    {
        if (Status != expected)
            throw new InvalidOperationException(
                $"Cannot {operation}: dispenser '{Name}' is in state '{Status}' (expected '{expected}').");
    }

    private void EnsureInventoryAvailable()
    {
        if (InventoryCount <= 0)
            throw new InvalidOperationException(
                $"Cannot dispense from '{Name}': inventory is empty.");
    }
}
