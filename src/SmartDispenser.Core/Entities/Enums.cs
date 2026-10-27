namespace SmartDispenser.Core.Entities;

public enum DispenserStatus
{
    /// <summary>Machine is powered off or unreachable.</summary>
    Offline,
    /// <summary>Machine is online and ready to dispense.</summary>
    Idle,
    /// <summary>Machine is actively dispensing medication.</summary>
    Dispensing,
    /// <summary>Machine has no medication left.</summary>
    InventoryEmpty,
    /// <summary>Machine reported an error; requires manual intervention.</summary>
    Error
}

public enum DispenseRequestStatus
{
    PendingVerification,    // Verifying prescription with HealthTech API
    PendingPayment,         // Prescription OK; awaiting payment confirmation
    DispatchingToMachine,   // Payment received; sending command via SignalR
    WaitingForMachine,      // Command sent; awaiting physical ACK
    Completed,              // Machine dispensed successfully
    Failed                  // Any terminal failure (reason stored)
}

public enum PaymentStatus
{
    Pending,
    Confirmed,
    Failed
}
