using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace DarbarESaltanat.Infrastructure.Persistence;

/// <summary>Device identity loaded from sync_state at startup (falls back to the first branch).</summary>
public sealed class DeviceContext : IDeviceContext
{
    public string DeviceId { get; private set; } = "";
    public string BranchUuid { get; private set; } = "";

    public void Load(SqliteConnection conn)
    {
        string? Get(string sql, string? key = null)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            if (key is not null) c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string;
        }
        DeviceId = Get("SELECT state_value FROM sync_state WHERE state_key=$k", SyncStateKeys.DeviceId)
                   ?? throw new InvalidOperationException("Device id missing from sync_state");
        BranchUuid = Get("SELECT state_value FROM sync_state WHERE state_key=$k", SyncStateKeys.BranchUuid)
                     ?? Get("SELECT uuid FROM branches WHERE deleted_at IS NULL ORDER BY id LIMIT 1")
                     ?? throw new InvalidOperationException("No branch configured");
    }

    /// <summary>Used by tests and by device registration (Settings → Sync).</summary>
    public void Set(string deviceId, string branchUuid) { DeviceId = deviceId; BranchUuid = branchUuid; }
}
