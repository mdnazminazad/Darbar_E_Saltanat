namespace DarbarESaltanat.Domain.Entities;

/// <summary>Row of the local sync_outbox queue (local-only, never synced itself).</summary>
public class SyncOutboxItem
{
    public long Id { get; set; }
    public string LocalId { get; set; } = Guid.NewGuid().ToString();
    public string EntityType { get; set; } = "";
    public string EntityUuid { get; set; } = "";
    public string Operation { get; set; } = Enums.SyncOperation.Create;
    public string PayloadJson { get; set; } = "{}";
    public int BaseVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int RetryCount { get; set; }
    public string Status { get; set; } = Enums.OutboxStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? BatchId { get; set; }
}

/// <summary>Key/value device state: device_id, last_pull_at, api_base_url, protected device token…</summary>
public class SyncStateEntry
{
    public string StateKey { get; set; } = "";
    public string? StateValue { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SyncLogEntry
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Level { get; set; } = "INFO";
    public string Direction { get; set; } = "SYSTEM";
    public string? BatchId { get; set; }
    public string Message { get; set; } = "";
    public string? Details { get; set; }
}

public static class SyncStateKeys
{
    public const string DeviceId = "device_id";
    public const string DeviceTokenProtected = "device_token_protected";
    public const string ApiBaseUrl = "api_base_url";
    public const string BranchUuid = "branch_uuid";
    public const string LastPullAt = "last_pull_at";
    public const string SyncIntervalSeconds = "sync_interval_seconds";
    public const string DeviceCode = "device_code";
    // Printers are per computer, so they live in local sync_state (never synced).
    public const string ReceiptPrinter = "receipt_printer";
    public const string KitchenPrinter = "kitchen_printer";
    /// <summary>Optional per-station override: "kitchen_printer:Grill".</summary>
    public const string KitchenPrinterPrefix = "kitchen_printer:";
    public const string CashDrawerEnabled = "cash_drawer_enabled";
}
