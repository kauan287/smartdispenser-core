namespace SmartDispenser.Core.Entities;

public sealed class Dispenser
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Location { get; private set; } = string.Empty;
    public string Medication { get; private set; } = string.Empty;
    public DispenserStatus Status { get; private set; }
    public int InventoryCount { get; private set; }
    public int InventoryCapacity { get; private set; }
    public DateTimeOffset LastHeartbeatAt { get; private set; }
    public DateTimeOffset? LastDispenseAt { get; private set; }
    public Guid? CurrentDispenseRequestId { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token: two writers that loaded the same version cannot both save.</summary>
    public int Version { get; private set; }

    private Dispenser() { }

    public static Dispenser Create(string name, string location, string medication, int inventoryCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(medication);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inventoryCapacity);

        var now = DateTimeOffset.UtcNow;
        return new Dispenser
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = location,
            Medication = medication,
            Status = DispenserStatus.Offline,
            InventoryCapacity = inventoryCapacity,
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool CanDispense() => Status == DispenserStatus.Idle;

    public bool Stocks(string medication) => string.Equals(Medication, medication, StringComparison.OrdinalIgnoreCase);

    public double InventoryPercentage => (double)InventoryCount / InventoryCapacity * 100;

    public bool IsStale(TimeSpan threshold) => DateTimeOffset.UtcNow - LastHeartbeatAt > threshold;

    public void BeginDispensing(Guid requestId)
    {
        EnsureStatus(DispenserStatus.Idle, "begin dispensing");
        CurrentDispenseRequestId = requestId;
        SetStatus(DispenserStatus.Dispensing);
    }

    public void CompleteDispensing(Guid requestId)
    {
        EnsureDispensing(requestId, "complete");
        InventoryCount = Math.Max(0, InventoryCount - 1);
        LastDispenseAt = DateTimeOffset.UtcNow;
        CurrentDispenseRequestId = null;
        SetStatus(ReadyStatus);
    }

    public void RecordLateDispense()
    {
        InventoryCount = Math.Max(0, InventoryCount - 1);
        LastDispenseAt = DateTimeOffset.UtcNow;
        SetStatus(Status is DispenserStatus.Idle or DispenserStatus.InventoryEmpty ? ReadyStatus : Status);
    }

    public void FailDispensing(Guid requestId)
    {
        EnsureDispensing(requestId, "fail");
        CurrentDispenseRequestId = null;
        SetStatus(DispenserStatus.Error);
    }

    public void GoOnline(string? firmwareVersion = null)
    {
        FirmwareVersion = firmwareVersion ?? FirmwareVersion;
        LastHeartbeatAt = DateTimeOffset.UtcNow;
        SetStatus(Status == DispenserStatus.Offline ? ReadyStatus : Status);
    }

    public void RecordHeartbeat()
    {
        LastHeartbeatAt = DateTimeOffset.UtcNow;
        if (Status == DispenserStatus.Offline)
            SetStatus(ReadyStatus);
    }

    public void GoOffline()
    {
        CurrentDispenseRequestId = null;
        SetStatus(DispenserStatus.Offline);
    }

    public void UpdateInventory(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, InventoryCapacity);
        InventoryCount = count;
        SetStatus(Status is DispenserStatus.Idle or DispenserStatus.InventoryEmpty ? ReadyStatus : Status);
    }

    private DispenserStatus ReadyStatus => InventoryCount == 0 ? DispenserStatus.InventoryEmpty : DispenserStatus.Idle;

    private void SetStatus(DispenserStatus status)
    {
        Status = status;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    private void EnsureStatus(DispenserStatus expected, string operation)
    {
        if (Status != expected)
            throw new InvalidOperationException(
                $"Cannot {operation}: dispenser '{Name}' is in state '{Status}' (expected '{expected}').");
    }

    private void EnsureDispensing(Guid requestId, string operation)
    {
        EnsureStatus(DispenserStatus.Dispensing, $"{operation} dispense");
        if (CurrentDispenseRequestId != requestId)
            throw new InvalidOperationException(
                $"Cannot {operation} dispense {requestId}: dispenser '{Name}' is dispensing {CurrentDispenseRequestId}.");
    }
}
