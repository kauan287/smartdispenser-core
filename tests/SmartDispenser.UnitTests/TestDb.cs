using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartDispenser.Infrastructure.Data;

namespace SmartDispenser.UnitTests;

public static class TestDb
{
    public static AppDbContext Create(string? name = null, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .AddInterceptors(interceptors)
            .Options);
}

/// <summary>Simulates the database rejecting any save that writes a dispenser row.</summary>
public sealed class FailDispenserWrites : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) =>
        eventData.Context!.ChangeTracker.Entries<Dispenser>().Any(e => e.State == EntityState.Modified)
            ? throw new DbUpdateException("simulated failure writing dispensers")
            : base.SavingChangesAsync(eventData, result, ct);
}
