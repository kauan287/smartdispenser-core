using Microsoft.EntityFrameworkCore;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;
using SmartDispenser.Infrastructure.Data;

namespace SmartDispenser.Infrastructure.Repositories;

public sealed class DispenserRepository(AppDbContext db) : IDispenserRepository
{
    public Task<Dispenser?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Dispensers.FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<Dispenser>> GetAllAsync(CancellationToken ct = default) =>
        await db.Dispensers.OrderBy(d => d.Name).ToListAsync(ct);

    public void Add(Dispenser dispenser) => db.Dispensers.Add(dispenser);
}

public sealed class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct = default) =>
        db.Payments.FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct);

    public void Add(Payment payment) => db.Payments.Add(payment);
}

public sealed class DispenseRequestRepository(AppDbContext db) : IDispenseRequestRepository
{
    public Task<DispenseRequest?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.DispenseRequests.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<bool> IsPrescriptionInUseAsync(string prescriptionId, CancellationToken ct = default) =>
        db.DispenseRequests.AnyAsync(r => r.PrescriptionId == prescriptionId && r.Status != DispenseRequestStatus.Failed, ct);

    public async Task<IReadOnlyList<DispenseRequest>> GetDispatchedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        await db.DispenseRequests.Where(r => r.Status == DispenseRequestStatus.Dispatched && r.UpdatedAt < cutoff).ToListAsync(ct);

    public void Add(DispenseRequest request) => db.DispenseRequests.Add(request);
}
