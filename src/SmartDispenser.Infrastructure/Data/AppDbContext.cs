using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Infrastructure.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<Dispenser> Dispensers => Set<Dispenser>();
    public DbSet<DispenseRequest> DispenseRequests => Set<DispenseRequest>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    async Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            return await SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflictException("Another command changed the same data first. Nothing was saved.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("Another command saved the same data first. Nothing was saved.", ex);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AddOutboxMessagesForFinishedRequests();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        AddOutboxMessagesForFinishedRequests();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    private void AddOutboxMessagesForFinishedRequests()
    {
        var finished = ChangeTracker.Entries<DispenseRequest>()
            .Where(e => e.State == EntityState.Modified
                && e.OriginalValues.GetValue<DispenseRequestStatus>(nameof(DispenseRequest.Status)) != e.Entity.Status
                && e.Entity.Status is DispenseRequestStatus.Completed or DispenseRequestStatus.Failed)
            .Select(e => e.Entity)
            .ToList();

        foreach (var request in finished)
            OutboxMessages.Add(OutboxMessage.Create(OutboxEvents.DispenseResult, new DispenseResultNotification(
                request.Id, request.Status == DispenseRequestStatus.Completed, request.FailureReason)));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Dispenser>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Name).HasMaxLength(100).IsRequired();
            e.Property(d => d.Location).HasMaxLength(200).IsRequired();
            e.Property(d => d.Medication).HasMaxLength(200).IsRequired();
            e.Property(d => d.Status)
                .HasConversion<string>()
                .HasMaxLength(30);
            e.Property(d => d.FirmwareVersion).HasMaxLength(50);
            e.Property(d => d.Version).IsConcurrencyToken();

            e.HasIndex(d => d.Status);
            e.HasIndex(d => d.LastHeartbeatAt);
        });

        modelBuilder.Entity<DispenseRequest>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.PrescriptionId).HasMaxLength(100).IsRequired();
            e.Property(r => r.PatientIdentifier).HasMaxLength(100).IsRequired();
            e.Property(r => r.Medication).HasMaxLength(200).IsRequired();
            e.Property(r => r.Dosage).HasMaxLength(DispenseRequest.DosageMaxLength).IsRequired();
            e.Property(r => r.Status)
                .HasConversion<string>()
                .HasMaxLength(40);
            e.Property(r => r.FailureReason).HasMaxLength(DispenseRequest.FailureReasonMaxLength);

            e.HasIndex(r => r.PrescriptionId)
                .IsUnique()
                .HasFilter($"\"{nameof(DispenseRequest.Status)}\" <> '{nameof(DispenseRequestStatus.Failed)}'");
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.CreatedAt);
        });

        modelBuilder.Entity<Payment>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.ExternalTransactionId).HasMaxLength(Payment.TransactionIdMaxLength).IsRequired();
            e.Property(p => p.IdempotencyKey).HasMaxLength(Payment.IdempotencyKeyMaxLength).IsRequired();
            e.Property(p => p.Currency).HasMaxLength(Payment.CurrencyMaxLength).IsRequired();
            e.Property(p => p.GatewayProvider).HasMaxLength(Payment.ProviderMaxLength).IsRequired();
            e.Property(p => p.GatewayResponse).HasMaxLength(Payment.GatewayResponseMaxLength);
            e.Property(p => p.Amount).HasColumnType("decimal(18,4)");
            e.Property(p => p.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            e.HasIndex(p => p.IdempotencyKey).IsUnique();
            e.HasIndex(p => p.ExternalTransactionId).IsUnique();
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.EventType).HasMaxLength(100).IsRequired();
            e.Property(o => o.Payload).HasColumnType("jsonb");
            e.Property(o => o.LastError).HasMaxLength(OutboxMessage.LastErrorMaxLength);
            e.Property(o => o.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            e.HasIndex(o => new { o.Status, o.NextRetryAt });
        });
    }
}
