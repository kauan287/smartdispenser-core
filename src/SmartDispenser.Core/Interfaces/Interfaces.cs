using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;

namespace SmartDispenser.Core.Interfaces;

public interface IDispenserRepository
{
    Task<Dispenser?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Dispenser>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(Dispenser dispenser, CancellationToken ct = default);
    Task UpdateAsync(Dispenser dispenser, CancellationToken ct = default);
}

public interface IPaymentRepository
{
    Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<Payment?> GetByExternalTransactionIdAsync(string externalId, CancellationToken ct = default);
    Task AddAsync(Payment payment, CancellationToken ct = default);
    Task UpdateAsync(Payment payment, CancellationToken ct = default);
}

public interface IDispenseRequestRepository
{
    Task<DispenseRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DispenseRequest>> GetRecentAsync(int count = 50, CancellationToken ct = default);
    Task AddAsync(DispenseRequest request, CancellationToken ct = default);
    Task UpdateAsync(DispenseRequest request, CancellationToken ct = default);
}

/// <summary>
/// Typed SignalR hub proxy — decouples business logic from SignalR infrastructure.
/// </summary>
public interface IDispenserHubContext
{
    Task SendCommandAsync(Guid dispenserId, DispenserCommand command, CancellationToken ct = default);
    Task BroadcastStateUpdateAsync(DispenserStateDto state, CancellationToken ct = default);
    Task NotifyDispenseResultAsync(Guid dispenserId, DispenseResultNotification result, CancellationToken ct = default);
}

/// <summary>HTTP client abstraction for the HealthTech Agent Orchestrator API.</summary>
public interface IHealthTechApiClient
{
    Task<PrescriptionVerificationResult> VerifyPrescriptionAsync(
        string prescriptionId, CancellationToken ct = default);
}

// SignalR message types
public sealed record DispenserCommand(CommandType Type, object? Payload = null);
public sealed record DispenseResultNotification(Guid RequestId, bool Success, string? ErrorMessage = null);

public enum CommandType
{
    Dispense,
    CancelDispense,
    RequestInventoryUpdate,
    Reboot,
    RunDiagnostics
}
