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

    public async Task AddAsync(Dispenser dispenser, CancellationToken ct = default)
    {
        db.Dispensers.Add(dispenser);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Dispenser dispenser, CancellationToken ct = default)
    {
        db.Dispensers.Update(dispenser);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct = default) =>
        db.Payments.FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct);

    public async Task AddAsync(Payment payment, CancellationToken ct = default)
    {
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class DispenseRequestRepository(AppDbContext db) : IDispenseRequestRepository
{
    public Task<DispenseRequest?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.DispenseRequests.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<bool> IsPrescriptionInUseAsync(string prescriptionId, CancellationToken ct = default) =>
        db.DispenseRequests.AnyAsync(r => r.PrescriptionId == prescriptionId && r.Status != DispenseRequestStatus.Failed, ct);

    public async Task AddAsync(DispenseRequest request, CancellationToken ct = default)
    {
        db.DispenseRequests.Add(request);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(DispenseRequest request, CancellationToken ct = default)
    {
        db.DispenseRequests.Update(request);
        await db.SaveChangesAsync(ct);
    }
}
