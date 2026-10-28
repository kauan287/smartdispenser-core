namespace SmartDispenser.Core.Entities;

public enum DispenserStatus
{
    Offline,
    Idle,
    Dispensing,
    InventoryEmpty,
    Error
}

public enum DispenseRequestStatus
{
    PendingPayment,
    Dispatched,
    Interrupted,
    Completed,
    Failed
}

public enum PaymentStatus
{
    Pending,
    Confirmed,
    Failed
}
