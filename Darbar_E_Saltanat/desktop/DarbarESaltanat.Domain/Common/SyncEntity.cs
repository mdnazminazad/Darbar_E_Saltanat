namespace DarbarESaltanat.Domain.Common;

/// <summary>
/// Base for every row that is replicated between the desktop SQLite database and the server.
/// <para><see cref="Id"/> is a LOCAL rowid only. <see cref="Uuid"/> is the global identifier.</para>
/// <para>Relations are expressed as "<c>XxxUuid</c>" properties referencing the parent's Uuid.</para>
/// </summary>
public abstract class SyncEntity
{
    public long Id { get; set; }
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    /// <summary>Logical branch_id. Stored as branch_uuid locally (null for chain-wide rows).</summary>
    public string? BranchUuid { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    /// <summary>The server version this row is based on (0 = never seen by the server).</summary>
    public int SyncVersion { get; set; }
    public string? DeviceId { get; set; }
    public string SyncStatus { get; set; } = Enums.RowSyncStatus.Pending;

    public bool IsDeleted => DeletedAt.HasValue;
}

/// <summary>Marks a table as append-only (payments, stock movements, audit logs): rows are never updated after creation.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AppendOnlyAttribute : Attribute { }

/// <summary>Marks financial/inventory tables: sync conflicts on these are never auto-resolved.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class FinancialRecordAttribute : Attribute { }

/// <summary>SQLite table name for the entity (also the sync "entity" name on the wire).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SyncTableAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
