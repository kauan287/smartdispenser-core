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
    Task AddAsync(Payment payment, CancellationToken ct = default);
}

public interface IDispenseRequestRepository
{
    Task<DispenseRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);
    /// <summary>True when the prescription has a dispense request that has not failed.</summary>
    Task<bool> IsPrescriptionInUseAsync(string prescriptionId, CancellationToken ct = default);
    Task AddAsync(DispenseRequest request, CancellationToken ct = default);
    Task UpdateAsync(DispenseRequest request, CancellationToken ct = default);
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
