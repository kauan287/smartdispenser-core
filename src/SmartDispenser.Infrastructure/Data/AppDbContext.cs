using Microsoft.EntityFrameworkCore;
using SmartDispenser.Core.Entities;

namespace SmartDispenser.Infrastructure.Data;

/// <summary>
/// Entity Framework Core DbContext.
/// Uses PostgreSQL via Npgsql. Connection string from IConfiguration.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Dispenser> Dispensers => Set<Dispenser>();
    public DbSet<DispenseRequest> DispenseRequests => Set<DispenseRequest>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Dispenser ────────────────────────────────────────────────────────
        modelBuilder.Entity<Dispenser>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Name).HasMaxLength(100).IsRequired();
            e.Property(d => d.Location).HasMaxLength(200).IsRequired();
            e.Property(d => d.Status)
                .HasConversion<string>()
                .HasMaxLength(30);
            e.Property(d => d.FirmwareVersion).HasMaxLength(50);

            e.HasIndex(d => d.Status);
            e.HasIndex(d => d.LastHeartbeatAt);
        });

        // ── DispenseRequest ──────────────────────────────────────────────────
        modelBuilder.Entity<DispenseRequest>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.PrescriptionId).HasMaxLength(100).IsRequired();
            e.Property(r => r.PatientIdentifier).HasMaxLength(100).IsRequired();
            e.Property(r => r.Medication).HasMaxLength(200).IsRequired();
            e.Property(r => r.Dosage).HasMaxLength(100).IsRequired();
            e.Property(r => r.Status)
                .HasConversion<string>()
                .HasMaxLength(40);
            e.Property(r => r.FailureReason).HasMaxLength(500);
            e.Property(r => r.CorrelationId).HasMaxLength(50);

            e.HasIndex(r => r.PrescriptionId);
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.CreatedAt);
        });

        // ── Payment ──────────────────────────────────────────────────────────
        modelBuilder.Entity<Payment>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.ExternalTransactionId).HasMaxLength(100).IsRequired();
            e.Property(p => p.IdempotencyKey).HasMaxLength(100).IsRequired();
            e.Property(p => p.Currency).HasMaxLength(10).IsRequired();
            e.Property(p => p.GatewayProvider).HasMaxLength(50).IsRequired();
            e.Property(p => p.GatewayResponse).HasMaxLength(1000);
            e.Property(p => p.Amount).HasColumnType("decimal(18,4)");
            e.Property(p => p.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            // Critical: unique constraint on idempotency key
            e.HasIndex(p => p.IdempotencyKey).IsUnique();
            e.HasIndex(p => p.ExternalTransactionId).IsUnique();
        });

        // ── OutboxMessage ────────────────────────────────────────────────────
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.EventType).HasMaxLength(100).IsRequired();
            e.Property(o => o.Payload).HasColumnType("jsonb"); // PostgreSQL native JSON
            e.Property(o => o.LastError).HasMaxLength(2000);
            e.Property(o => o.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            // Partial index: only query Pending messages that are due
            e.HasIndex(o => new { o.Status, o.NextRetryAt });
        });
    }
}
