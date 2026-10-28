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

public sealed class UniqueIndexRejectsDispenseRequests : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) =>
        eventData.Context!.ChangeTracker.Entries<DispenseRequest>().Any(e => e.State == EntityState.Added)
            ? throw new DbUpdateException("simulated unique violation", new Npgsql.PostgresException(
                "duplicate key value violates unique constraint", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation))
            : base.SavingChangesAsync(eventData, result, ct);
}

public sealed class FailDispenserWrites : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) =>
        eventData.Context!.ChangeTracker.Entries<Dispenser>().Any(e => e.State == EntityState.Modified)
            ? throw new DbUpdateException("simulated failure writing dispensers")
            : base.SavingChangesAsync(eventData, result, ct);
}
