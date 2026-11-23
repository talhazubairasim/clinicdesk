using System.Text.Json;
using ClinicDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ClinicDesk.Api.Infrastructure;

public class ClinicDbContext(DbContextOptions<ClinicDbContext> options, TenantContext tenant, TimeProvider clock) : DbContext(options)
{
    /// <summary>Referenced by the global query filters; EF evaluates it per query.</summary>
    public Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<StaffMember> Staff => Set<StaffMember>();
    public DbSet<Practitioner> Practitioners => Set<Practitioner>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Slug).HasMaxLength(40);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Plan).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Ignore(x => x.Limits);
            e.Ignore(x => x.Zone);
            e.Property(x => x.Charges).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<SubscriptionCharge>>(v, Json) ?? new(),
                ListComparer<SubscriptionCharge>());
        });

        b.Entity<StaffMember>(e =>
        {
            e.ToTable("staff");
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.FullName).HasMaxLength(120);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        b.Entity<Practitioner>(e =>
        {
            e.ToTable("practitioners");
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Specialty).HasMaxLength(120);
            e.Property(x => x.Hours).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<WorkingHours>>(v, Json) ?? new(),
                ListComparer<WorkingHours>());
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        b.Entity<Patient>(e =>
        {
            e.ToTable("patients");
            e.HasIndex(x => new { x.TenantId, x.LastName });
            e.Property(x => x.FirstName).HasMaxLength(80);
            e.Property(x => x.LastName).HasMaxLength(80);
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Ignore(x => x.FullName);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        b.Entity<Appointment>(e =>
        {
            e.ToTable("appointments");
            e.HasIndex(x => new { x.TenantId, x.PractitionerId, x.StartsAt });
            e.HasIndex(x => new { x.Status, x.StartsAt });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.Version).IsRowVersion();
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Practitioner>().WithMany().HasForeignKey(x => x.PractitionerId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        b.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.Property(x => x.Number).HasMaxLength(30);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TaxRate).HasPrecision(5, 4);
            e.Ignore(x => x.Subtotal);
            e.Ignore(x => x.Tax);
            e.Ignore(x => x.Total);
            e.Ignore(x => x.AmountPaid);
            e.Ignore(x => x.Balance);
            e.Property(x => x.Lines).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<InvoiceLine>>(v, Json) ?? new(),
                ListComparer<InvoiceLine>());
            e.Property(x => x.Payments).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<Payment>>(v, Json) ?? new(),
                ListComparer<Payment>());
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit");
            e.HasIndex(x => new { x.TenantId, x.At });
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }

    private static ValueComparer<List<T>> ListComparer<T>() => new(
        (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
        v => JsonSerializer.Serialize(v, Json).GetHashCode(),
        v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, Json), Json)!);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        StampTenantAndAudit();
        return base.SaveChangesAsync(ct);
    }

    public override int SaveChanges()
    {
        StampTenantAndAudit();
        return base.SaveChanges();
    }

    /// <summary>
    /// New rows inherit the request's tenant; writing another tenant's row is a bug and throws.
    /// Every change to a clinic-owned record is written to the audit trail.
    /// </summary>
    private void StampTenantAndAudit()
    {
        var now = clock.GetUtcNow();
        var entries = ChangeTracker.Entries<ITenantOwned>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && e.Entity is not AuditEntry)
            .ToList();
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenant.TenantId
                    ?? throw new InvalidOperationException($"No tenant for new {entry.Entity.GetType().Name}.");
            }
            else if (tenant.TenantId is { } current && entry.Entity.TenantId != current)
            {
                throw new InvalidOperationException("Attempted to write another clinic's data.");
            }

            Audit.Add(new AuditEntry
            {
                TenantId = entry.Entity.TenantId,
                UserId = tenant.UserId,
                Action = entry.State.ToString(),
                Entity = entry.Entity.GetType().Name,
                EntityId = entry.Property("Id").CurrentValue?.ToString() ?? "",
                At = now,
            });
        }
    }
}
