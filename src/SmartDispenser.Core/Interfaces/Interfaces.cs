using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;

namespace SmartDispenser.Core.Interfaces;

public interface IDispenserRepository
{
    Task<Dispenser?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Dispenser>> GetAllAsync(CancellationToken ct = default);
    void Add(Dispenser dispenser);
}

public interface IPaymentRepository
{
    Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    void Add(Payment payment);
}

public interface IDispenseRequestRepository
{
    Task<DispenseRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);
    /// <summary>True when the prescription has a dispense request that has not failed.</summary>
    Task<bool> IsPrescriptionInUseAsync(string prescriptionId, CancellationToken ct = default);
    void Add(DispenseRequest request);
}

/// <summary>
/// Commits every change tracked in the current scope in one transaction. Repositories only
/// load and track entities; each command decides when its changes become durable.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Pushes commands to machines and state changes to dashboards.</summary>
public interface IDispenserNotifier
{
    Task SendDispenseCommandAsync(Guid dispenserId, DispenseCommand command, CancellationToken ct = default);
    Task PublishStateAsync(DispenserStateDto state, CancellationToken ct = default);
    Task PublishDispenseResultAsync(DispenseResultNotification result, CancellationToken ct = default);
}

public interface IHealthTechApiClient
{
    /// <returns>The prescription, or null when the orchestrator does not know it.</returns>
    Task<PrescriptionVerificationResult?> VerifyPrescriptionAsync(string prescriptionId, CancellationToken ct = default);
}

public sealed record DispenseCommand(Guid RequestId, string PrescriptionId, string Medication, string Dosage);
public sealed record DispenseResultNotification(Guid RequestId, bool Success, string? ErrorMessage = null);
