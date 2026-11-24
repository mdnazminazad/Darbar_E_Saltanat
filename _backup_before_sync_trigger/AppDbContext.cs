using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Sync;
using DarbarESaltanat.Domain.Common;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DarbarESaltanat.Infrastructure.Persistence;

/// <summary>
/// Local SQLite context. Every SaveChanges on a <see cref="SyncEntity"/>:
///  1. stamps created_at / updated_at / device_id / sync_status=PENDING,
///  2. converts hard deletes into soft deletes (deleted_at),
///  3. writes a sync_outbox row for the change,
/// all inside the SAME SQLite transaction that EF uses for SaveChanges.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, IClock clock, IDeviceContext device, SqliteWriteGate gate)
    : DbContext(options)
{
    /// <summary>Set by the pull-sync applier: rows are written as SYNCED and NOT re-queued to the outbox.</summary>
    public bool ApplyingRemoteChanges { get; set; }

    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SyncOutboxItem> SyncOutbox => Set<SyncOutboxItem>();
    public DbSet<SyncStateEntry> SyncState => Set<SyncStateEntry>();
    public DbSet<SyncLogEntry> SyncLog => Set<SyncLogEntry>();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    protected override void OnModelCreating(ModelBuilder b)
    {
        foreach (var type in typeof(SyncEntity).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(SyncEntity))))
        {
            var table = type.GetCustomAttribute<SyncTableAttribute>()?.Name
                        ?? throw new InvalidOperationException($"{type.Name} is missing [SyncTable]");
            var e = b.Entity(type);
            e.ToTable(table);
            e.HasKey(nameof(SyncEntity.Id));
            e.HasIndex(nameof(SyncEntity.Uuid)).IsUnique();
            e.Ignore(nameof(SyncEntity.IsDeleted));
            if (type == typeof(Branch)) e.Ignore(nameof(SyncEntity.BranchUuid)); // the branches table IS the branch
            // Global soft-delete filter: deleted rows are invisible unless IgnoreQueryFilters() is used.
            var p = Expression.Parameter(type, "x");
            var body = Expression.Equal(Expression.Property(p, nameof(SyncEntity.DeletedAt)), Expression.Constant(null, typeof(DateTime?)));
            e.HasQueryFilter(Expression.Lambda(body, p));
        }

        b.Entity<SyncOutboxItem>(e => { e.ToTable("sync_outbox"); e.HasKey(x => x.Id); e.HasIndex(x => x.LocalId).IsUnique(); });
        b.Entity<SyncStateEntry>(e => { e.ToTable("sync_state"); e.HasKey(x => x.StateKey); });
        b.Entity<SyncLogEntry>(e => { e.ToTable("sync_log"); e.HasKey(x => x.Id); });

        b.Entity<KotItem>().HasOne<Kot>().WithMany().HasPrincipalKey(k => k.Uuid).HasForeignKey(ki => ki.KotUuid);
        b.Entity<Kot>().HasOne<Order>().WithMany().HasPrincipalKey(o => o.Uuid).HasForeignKey(k => k.OrderUuid);
        b.Entity<OrderItem>().HasOne<Order>().WithMany().HasPrincipalKey(o => o.Uuid).HasForeignKey(oi => oi.OrderUuid);
        b.Entity<OrderItemAddon>().HasOne<OrderItem>().WithMany().HasPrincipalKey(oi => oi.Uuid).HasForeignKey(a => a.OrderItemUuid);
        b.Entity<Payment>().HasOne<Order>().WithMany().HasPrincipalKey(o => o.Uuid).HasForeignKey(p => p.OrderUuid);

        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties())
        {
            prop.SetColumnName(Naming.ToSnake(prop.Name));
            if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(UtcText.Converter);
            else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(UtcText.NullableConverter);
        }
    }

    /// <summary>For bulk ExecuteUpdate/ExecuteDelete statements that bypass SaveChanges.</summary>
    public Task<IDisposable> AcquireWriteLockAsync(CancellationToken ct = default) => gate.AcquireAsync(ct);

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync (writes go through the single-writer gate).");

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        using var _ = await gate.AcquireAsync(ct);
        PrepareSyncChanges();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    private void PrepareSyncChanges()
    {
        var now = clock.UtcNow;
        var entries = ChangeTracker.Entries<SyncEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();

        foreach (var entry in entries)
        {
            var op = entry.State switch { EntityState.Added => SyncOperation.Create, EntityState.Deleted => SyncOperation.Delete, _ => SyncOperation.Update };
            if (entry.State == EntityState.Deleted) { entry.State = EntityState.Modified; entry.Entity.DeletedAt = now; }
            else if (op == SyncOperation.Update && entry.Entity.DeletedAt.HasValue && entry.Property(x => x.DeletedAt).IsModified
                     && entry.Property(x => x.DeletedAt).OriginalValue is null) op = SyncOperation.Delete;

            if (ApplyingRemoteChanges) { entry.Entity.SyncStatus = RowSyncStatus.Synced; continue; }

            if (op != SyncOperation.Create && entry.Metadata.ClrType.GetCustomAttribute<AppendOnlyAttribute>() is not null)
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} is append-only; post a reversing entry instead of editing it.");

            var e = entry.Entity;
            if (op == SyncOperation.Create) { if (e.CreatedAt == default) e.CreatedAt = now; e.SyncVersion = 0; }
            e.UpdatedAt = now;
            e.DeviceId = device.DeviceId;
            e.SyncStatus = RowSyncStatus.Pending;
            var table = entry.Metadata.GetTableName()!;
            if (e.BranchUuid is null && table != "branches" && !GlobalTables.Contains(table)) e.BranchUuid = device.BranchUuid;

            QueueOutbox(entry, op, now);
        }
    }

    // Global-scope tables (roles, users, settings…) may legitimately have a NULL branch; others default to this device's branch.
    private static readonly HashSet<string> GlobalTables = ["roles", "permissions", "role_permissions", "users", "user_roles", "settings", "audit_logs", "customers", "customer_addresses", "loyalty_transactions"];

    private void QueueOutbox(EntityEntry<SyncEntity> entry, string op, DateTime now)
    {
        var table = entry.Metadata.GetTableName()!;
        var payload = JsonSerializer.Serialize(SyncPayload.Snapshot(entry.Entity), Json);
        var uuid = entry.Entity.Uuid;

        // Coalesce with a not-yet-sent entry for the same row: one CREATE (or UPDATE) carrying the latest state.
        var pending = SyncOutbox.Local.FirstOrDefault(o => o.EntityUuid == uuid && o.EntityType == table && o.Status == OutboxStatus.Pending)
                      ?? SyncOutbox.FirstOrDefault(o => o.EntityUuid == uuid && o.EntityType == table && o.Status == OutboxStatus.Pending);
        if (pending is not null)
        {
            pending.PayloadJson = payload;
            pending.UpdatedAt = now;
            if (pending.Operation != SyncOperation.Create) pending.Operation = op;
            else if (op == SyncOperation.Delete) pending.Operation = SyncOperation.Create; // created then deleted offline: send CREATE with deleted_at set
            return;
        }
        SyncOutbox.Add(new SyncOutboxItem
        {
            EntityType = table, EntityUuid = uuid, Operation = op, PayloadJson = payload,
            BaseVersion = entry.Entity.SyncVersion, CreatedAt = now, UpdatedAt = now, NextAttemptAt = now,
        });
    }
}

public interface IAppDbContextFactory
{
    AppDbContext Create();
}

public sealed class AppDbContextFactory(DbContextOptions<AppDbContext> options, IClock clock, IDeviceContext device, SqliteWriteGate gate) : IAppDbContextFactory
{
    public AppDbContext Create() => new(options, clock, device, gate);
}
