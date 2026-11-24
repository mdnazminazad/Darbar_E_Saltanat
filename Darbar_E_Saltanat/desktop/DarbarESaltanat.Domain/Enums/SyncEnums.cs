namespace DarbarESaltanat.Domain.Enums;

/// <summary>Per-row sync state stored in every syncable SQLite table.</summary>
public static class RowSyncStatus
{
    public const string Pending = "PENDING";
    public const string Synced = "SYNCED";
    public const string Conflict = "CONFLICT";
    public const string Failed = "FAILED";
}

/// <summary>Status of an entry in the local sync_outbox queue.</summary>
public static class OutboxStatus
{
    public const string Pending = "PENDING";
    public const string Syncing = "SYNCING";
    public const string Synced = "SYNCED";
    public const string Failed = "FAILED";
    public const string Conflict = "CONFLICT";
}

public static class SyncOperation
{
    public const string Create = "CREATE";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
}

/// <summary>What the desktop shows in its status bar.</summary>
public enum SyncHealth { NotConfigured, Offline, Online, Syncing, Synced, PendingChanges, Failed, Conflicts, Unauthorized }
