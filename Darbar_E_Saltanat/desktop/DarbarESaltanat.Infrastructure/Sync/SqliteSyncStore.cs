using System.Globalization;
using System.Text.Json;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Sync;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace DarbarESaltanat.Infrastructure.Sync;

/// <summary>
/// SQLite implementation of the sync bookkeeping. Every write goes through <see cref="SqliteWriteGate"/>
/// (the same single-writer lock EF uses) and runs in a short transaction; nothing here touches the network.
/// Table and column names are only ever taken from the live schema (sqlite_master / PRAGMA table_info).
/// </summary>
public sealed class SqliteSyncStore(SqliteConnectionString cs, SqliteWriteGate gate, ISecretProtector? protector = null) : ISyncStore
{
    private static readonly string[] Unsynced = ["PENDING", "SYNCING", "FAILED"];
    private Dictionary<string, HashSet<string>>? _schema;

    private static string Ts(DateTime d) => SyncPayload.FormatUtc(d);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct, bool foreignKeys = true)
    {
        var c = new SqliteConnection(cs.Value);
        await c.OpenAsync(ct);
        await using var p = c.CreateCommand();
        p.CommandText = $"PRAGMA busy_timeout=5000; PRAGMA foreign_keys={(foreignKeys ? "ON" : "OFF")};";
        await p.ExecuteNonQueryAsync(ct);
        return c;
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private async Task<Dictionary<string, HashSet<string>>> SchemaAsync(SqliteConnection c, CancellationToken ct)
    {
        if (_schema is not null) return _schema;
        var tables = new List<string>();
        await using (var cmd = Cmd(c, null, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) tables.Add(r.GetString(0));
        var schema = new Dictionary<string, HashSet<string>>();
        foreach (var t in tables)
        {
            var cols = new HashSet<string>();
            await using var cmd = Cmd(c, null, $"PRAGMA table_info(\"{t}\")");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) cols.Add(r.GetString(1));
            if (cols.Contains("uuid") && cols.Contains("sync_version")) schema[t] = cols; // syncable tables only
        }
        return _schema = schema;
    }

    // ------------------------------------------------------------------ config & state

    public async Task<SyncConfig> GetConfigAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        async Task<string?> State(string key)
        {
            await using var cmd = Cmd(c, null, "SELECT state_value FROM sync_state WHERE state_key=$k", ("$k", key));
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        async Task<int> Setting(string key, int fallback, int min, int max)
        {
            await using var cmd = Cmd(c, null, "SELECT setting_value FROM settings WHERE setting_key=$k AND deleted_at IS NULL ORDER BY branch_uuid IS NULL LIMIT 1", ("$k", key));
            return int.TryParse(await cmd.ExecuteScalarAsync(ct) as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;
        }
        var token = protector?.Unprotect(await State(SyncStateKeys.DeviceTokenProtected));
        return new SyncConfig(await State(SyncStateKeys.ApiBaseUrl), await State(SyncStateKeys.DeviceId) ?? "", token,
            await State(SyncStateKeys.LastPullAt), await Setting("sync_batch_size", 100, 10, 500), await Setting("sync_interval_seconds", 30, 10, 3600));
    }

    public async Task SetLastPullAsync(string since, CancellationToken ct = default)
    {
        using var _ = await gate.AcquireAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, null, "INSERT INTO sync_state (state_key, state_value, updated_at) VALUES ($k,$v,$t) " +
            "ON CONFLICT(state_key) DO UPDATE SET state_value=excluded.state_value, updated_at=excluded.updated_at",
            ("$k", SyncStateKeys.LastPullAt), ("$v", since), ("$t", Ts(DateTime.UtcNow)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ push side

    public async Task<OutboxBatch?> ClaimBatchAsync(int max, DateTime nowUtc, CancellationToken ct = default)
    {
        using var _ = await gate.AcquireAsync(ct);
        await using var c = await OpenAsync(ct);
        var schema = await SchemaAsync(c, ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);

        string? resumed;
        await using (var cmd = Cmd(c, tx, "SELECT batch_id FROM sync_outbox WHERE status='SYNCING' AND batch_id IS NOT NULL ORDER BY id LIMIT 1"))
            resumed = await cmd.ExecuteScalarAsync(ct) as string;

        var entries = new List<OutboxEntry>();
        var batchId = resumed ?? Guid.NewGuid().ToString();
        var sql = resumed is not null
            ? "SELECT id, entity_type, entity_uuid, operation, payload_json, base_version, retry_count FROM sync_outbox WHERE batch_id=$b AND status='SYNCING' ORDER BY id"
            : "SELECT id, entity_type, entity_uuid, operation, payload_json, base_version, retry_count FROM sync_outbox " +
              "WHERE status IN ('PENDING','FAILED') AND (next_attempt_at IS NULL OR next_attempt_at <= $now) ORDER BY id LIMIT $max";
        await using (var cmd = Cmd(c, tx, sql, ("$b", batchId), ("$now", Ts(nowUtc)), ("$max", max)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                entries.Add(new OutboxEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6)));
        if (entries.Count == 0) { await tx.RollbackAsync(ct); return null; }

        // The base version is the server version this device currently knows for the row
        // (pull never overwrites a row with unsynced edits, so local state includes every version it has seen).
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Operation == "CREATE" || !schema.ContainsKey(e.EntityType)) continue;
            await using var cmd = Cmd(c, tx, $"SELECT sync_version FROM \"{e.EntityType}\" WHERE uuid=$u", ("$u", e.EntityUuid));
            if (await cmd.ExecuteScalarAsync(ct) is long v && v > e.BaseVersion) entries[i] = e with { BaseVersion = (int)v };
        }
        if (resumed is null)
        {
            await using var upd = Cmd(c, tx, $"UPDATE sync_outbox SET status='SYNCING', batch_id=$b, last_attempt_at=$now, updated_at=$now WHERE id IN ({string.Join(",", entries.Select(e => e.Id))})",
                ("$b", batchId), ("$now", Ts(nowUtc)));
            await upd.ExecuteNonQueryAsync(ct);
        }
        foreach (var e in entries)
        {
            await using var b = Cmd(c, tx, "UPDATE sync_outbox SET base_version=$v WHERE id=$id", ("$v", e.BaseVersion), ("$id", e.Id));
            await b.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new OutboxBatch(batchId, entries, resumed is not null);
    }

    public async Task ApplyPushResultsAsync(OutboxBatch batch, IReadOnlyList<PushResult> results, DateTime nowUtc, CancellationToken ct = default)
    {
        using var _ = await gate.AcquireAsync(ct);
        await using var c = await OpenAsync(ct);
        var schema = await SchemaAsync(c, ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        var now = Ts(nowUtc);
        async Task Exec(string sql, params (string, object?)[] a) { await using var cmd = Cmd(c, tx, sql, a); await cmd.ExecuteNonQueryAsync(ct); }

        for (var i = 0; i < batch.Entries.Count; i++)
        {
            var e = batch.Entries[i];
            var r = results[i];
            var known = schema.ContainsKey(e.EntityType);
            if (r.Uuid != e.EntityUuid) r = r with { Status = "FAILED", Reason = "Result does not match the change sent" };
            switch (r.Status)
            {
                case "SYNCED":
                    var v = r.ServerVersion ?? e.BaseVersion;
                    await Exec("UPDATE sync_outbox SET status='SYNCED', error_message=NULL, updated_at=$now WHERE id=$id", ("$now", now), ("$id", e.Id));
                    // Older unsent snapshots of the same row are obsolete; newer ones continue from the new server version.
                    await Exec("UPDATE sync_outbox SET status='SYNCED', error_message='superseded', updated_at=$now WHERE entity_uuid=$u AND id<$id AND status IN ('PENDING','FAILED')",
                        ("$now", now), ("$u", e.EntityUuid), ("$id", e.Id));
                    await Exec("UPDATE sync_outbox SET base_version=$v WHERE entity_uuid=$u AND id>$id AND status IN ('PENDING','FAILED') AND base_version<$v",
                        ("$v", v), ("$u", e.EntityUuid), ("$id", e.Id));
                    if (known)
                        await Exec($"UPDATE \"{e.EntityType}\" SET sync_version=MAX(sync_version,$v), sync_status=CASE WHEN EXISTS " +
                                   "(SELECT 1 FROM sync_outbox WHERE entity_uuid=$u AND id<>$id AND status IN ('PENDING','SYNCING','FAILED','CONFLICT')) THEN sync_status ELSE 'SYNCED' END WHERE uuid=$u",
                            ("$v", v), ("$u", e.EntityUuid), ("$id", e.Id));
                    break;
                case "CONFLICT":
                    // base_version now records the server version we conflicted with: pulls of that version are held back
                    // until an admin resolves the conflict on the server (which publishes a higher version).
                    await Exec("UPDATE sync_outbox SET status='CONFLICT', base_version=COALESCE($sv, base_version), error_message=$err, updated_at=$now WHERE id=$id",
                        ("$sv", r.ServerVersion), ("$err", r.Reason ?? "Conflict with a newer server version"), ("$now", now), ("$id", e.Id));
                    if (known) await Exec($"UPDATE \"{e.EntityType}\" SET sync_status='CONFLICT' WHERE uuid=$u", ("$u", e.EntityUuid));
                    await Exec("INSERT INTO sync_conflicts_local (entity_type, entity_uuid, local_json, server_json, reason, status, detected_at) VALUES ($t,$u,$l,NULL,$r,'OPEN',$now)",
                        ("$t", e.EntityType), ("$u", e.EntityUuid), ("$l", e.PayloadJson), ("$r", r.Reason), ("$now", now));
                    break;
                default:
                    var retry = e.RetryCount + 1;
                    await Exec("UPDATE sync_outbox SET status='FAILED', retry_count=$rc, error_message=$err, next_attempt_at=$next, batch_id=NULL, updated_at=$now WHERE id=$id",
                        ("$rc", retry), ("$err", r.Reason ?? "Rejected by the server"), ("$next", Ts(nowUtc + SyncBackoff.ForItem(retry))), ("$now", now), ("$id", e.Id));
                    if (known) await Exec($"UPDATE \"{e.EntityType}\" SET sync_status='FAILED' WHERE uuid=$u AND sync_status<>'CONFLICT'", ("$u", e.EntityUuid));
                    break;
            }
        }
        await tx.CommitAsync(ct);
    }

    public async Task MarkBatchErrorAsync(OutboxBatch batch, string error, DateTime nowUtc, CancellationToken ct = default)
    {
        using var _ = await gate.AcquireAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, null, "UPDATE sync_outbox SET error_message=$e, last_attempt_at=$now, updated_at=$now WHERE batch_id=$b AND status='SYNCING'",
            ("$e", Truncate(error)), ("$now", Ts(nowUtc)), ("$b", batch.BatchId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RejectBatchAsync(OutboxBatch batch, string error, DateTime nowUtc, CancellationToken ct = default)
    {
        using var _ = await gate.AcquireAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        foreach (var e in batch.Entries)
        {
            var retry = e.RetryCount + 1;
            await using var cmd = Cmd(c, tx, "UPDATE sync_outbox SET status='FAILED', batch_id=NULL, retry_count=$rc, error_message=$e, next_attempt_at=$next, updated_at=$now WHERE id=$id",
                ("$rc", retry), ("$e", Truncate(error)), ("$next", Ts(nowUtc + SyncBackoff.ForItem(retry))), ("$now", Ts(nowUtc)), ("$id", e.Id));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------ pull side

    /// <summary>
    /// Upserts server rows by uuid. A row is skipped while this device still has unsynced edits for it
    /// (the push decides: synced or conflict), and while it is in an unresolved conflict (the server version is
    /// not newer than the one it conflicted with). Otherwise it is written when the server version is newer —
    /// which is also how an admin's resolution reaches the till.
    /// FK checks are off during the apply: the server is authoritative and a partial history may reference rows
    /// that are older than this device's history window.
    /// </summary>
    public async Task<PullApplyResult> ApplyPullAsync(IReadOnlyList<PullEntity> changes, DateTime nowUtc, CancellationToken ct = default)
    {
        if (changes.Count == 0) return new PullApplyResult(0, 0, []);
        using var _ = await gate.AcquireAsync(ct);
        await using var c = await OpenAsync(ct, foreignKeys: false);
        var schema = await SchemaAsync(c, ct);
        int applied = 0, skipped = 0;
        var touched = new HashSet<string>();
        await using (var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct))
        {
            foreach (var group in changes)
            {
                if (!schema.TryGetValue(group.Entity, out var columns)) { skipped += group.Rows.Count; continue; }
                foreach (var row in group.Rows)
                {
                    if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("uuid", out var uuidEl) || uuidEl.GetString() is not { } uuid) { skipped++; continue; }
                    var incoming = row.TryGetProperty("sync_version", out var sv) && sv.TryGetInt64(out var svl) ? svl : 0;
                    await using (var pending = Cmd(c, tx, "SELECT COUNT(*) FROM sync_outbox WHERE entity_uuid=$u AND " +
                        "(status IN ('PENDING','SYNCING','FAILED') OR (status='CONFLICT' AND base_version >= $v))", ("$u", uuid), ("$v", incoming)))
                        if ((long)(await pending.ExecuteScalarAsync(ct))! > 0) { skipped++; continue; }

                    var cols = row.EnumerateObject().Where(p => columns.Contains(p.Name) && p.Name is not ("id" or "sync_status")).ToList();
                    var names = cols.Select(p => $"\"{p.Name}\"").Append("\"sync_status\"").ToList();
                    var values = cols.Select((_, i) => $"$p{i}").Append("'SYNCED'").ToList();
                    var updates = cols.Where(p => p.Name != "uuid").Select(p => $"\"{p.Name}\"=excluded.\"{p.Name}\"").Append("\"sync_status\"='SYNCED'");
                    var sql = $"INSERT INTO \"{group.Entity}\" ({string.Join(",", names)}) VALUES ({string.Join(",", values)}) " +
                              $"ON CONFLICT(\"uuid\") DO UPDATE SET {string.Join(",", updates)} " +
                              $"WHERE excluded.\"sync_version\" > \"{group.Entity}\".\"sync_version\" OR \"{group.Entity}\".\"sync_status\"='CONFLICT'";
                    await using var cmd = Cmd(c, tx, sql);
                    for (var i = 0; i < cols.Count; i++) cmd.Parameters.AddWithValue($"$p{i}", ToDb(cols[i].Value));
                    int n;
                    try { n = await cmd.ExecuteNonQueryAsync(ct); }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // constraint (e.g. a local row with the same unique name)
                    {
                        skipped++;
                        await LogInTxAsync(c, tx, "WARN", $"Skipped {group.Entity} {uuid}: {ex.Message}", ct);
                        continue;
                    }
                    if (n == 0) { skipped++; continue; }
                    applied++;
                    touched.Add(group.Entity);
                    // A newer server version also settles any local conflict on this row.
                    await using (var r1 = Cmd(c, tx, "UPDATE sync_outbox SET status='SYNCED', error_message='resolved on server', updated_at=$now WHERE entity_uuid=$u AND status='CONFLICT'", ("$u", uuid), ("$now", Ts(nowUtc))))
                        await r1.ExecuteNonQueryAsync(ct);
                    await using (var r2 = Cmd(c, tx, "UPDATE sync_conflicts_local SET status='RESOLVED', server_json=$s, resolved_at=$now WHERE entity_uuid=$u AND status='OPEN'", ("$u", uuid), ("$s", row.GetRawText()), ("$now", Ts(nowUtc))))
                        await r2.ExecuteNonQueryAsync(ct);
                }
            }
            await tx.CommitAsync(ct);
        }
        await using (var on = Cmd(c, null, "PRAGMA foreign_keys=ON")) await on.ExecuteNonQueryAsync(ct);
        return new PullApplyResult(applied, skipped, touched);
    }

    private static object ToDb(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDecimal().ToString(CultureInfo.InvariantCulture),
        _ => v.GetRawText(),
    };

    // ------------------------------------------------------------------ status, log, housekeeping

    public async Task<SyncCounts> CountsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var counts = new Dictionary<string, int>();
        await using var cmd = Cmd(c, null, "SELECT status, COUNT(*) FROM sync_outbox WHERE status<>'SYNCED' GROUP BY status");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) counts[r.GetString(0)] = r.GetInt32(1);
        int Get(string s) => counts.TryGetValue(s, out var n) ? n : 0;
        return new SyncCounts(Get("PENDING"), Get("SYNCING"), Get("FAILED"), Get("CONFLICT"));
    }

    public async Task LogAsync(string level, string direction, string? batchId, string message, string? details = null, CancellationToken ct = default)
    {
        using var _ = await gate.AcquireAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, null, "INSERT INTO sync_log (occurred_at, level, direction, batch_id, message, details) VALUES ($t,$l,$d,$b,$m,$x)",
            ("$t", Ts(DateTime.UtcNow)), ("$l", level), ("$d", direction), ("$b", batchId), ("$m", Truncate(message)), ("$x", details is null ? null : Truncate(details, 4000)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task LogInTxAsync(SqliteConnection c, SqliteTransaction tx, string level, string message, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, "INSERT INTO sync_log (occurred_at, level, direction, message) VALUES ($t,$l,'PULL',$m)",
            ("$t", Ts(DateTime.UtcNow)), ("$l", level), ("$m", Truncate(message)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SyncLogLine>> RecentLogAsync(int count, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var list = new List<SyncLogLine>();
        await using var cmd = Cmd(c, null, "SELECT occurred_at, level, direction, batch_id, message, details FROM sync_log ORDER BY id DESC LIMIT $n", ("$n", count));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SyncLogLine(UtcText.Read(r.GetString(0)), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public async Task<IReadOnlyList<OutboxProblem>> ProblemsAsync(int count, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var list = new List<OutboxProblem>();
        await using var cmd = Cmd(c, null, "SELECT id, entity_type, entity_uuid, operation, status, retry_count, error_message, next_attempt_at FROM sync_outbox " +
                                            "WHERE status IN ('FAILED','CONFLICT') ORDER BY id DESC LIMIT $n", ("$n", count));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new OutboxProblem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : UtcText.Read(r.GetString(7))));
        return list;
    }

    public async Task<int> RetryFailedAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        using var _ = await gate.AcquireAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, null, "UPDATE sync_outbox SET next_attempt_at=$now, updated_at=$now WHERE status='FAILED'", ("$now", Ts(nowUtc)));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task HousekeepAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        using var _ = await gate.AcquireAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, null,
            "DELETE FROM sync_outbox WHERE status='SYNCED' AND updated_at < $cut; " +
            "DELETE FROM sync_log WHERE id < (SELECT MAX(id) - 5000 FROM sync_log);",
            ("$cut", Ts(nowUtc.AddDays(-7))));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Truncate(string s, int max = 1000) => s.Length <= max ? s : s[..max];
}
