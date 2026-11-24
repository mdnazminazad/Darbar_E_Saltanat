using System.Reflection;
using DarbarESaltanat.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DarbarESaltanat.Infrastructure.Persistence;

/// <summary>Creates the local database on first run from the embedded schema + seed, so a new install works fully offline.</summary>
public sealed class DatabaseInitializer(ILogger<DatabaseInitializer> log)
{
    public const int CurrentSchemaVersion = 1;

    public static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"Embedded resource {name} not found");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public async Task InitializeAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using (var p = conn.CreateCommand()) { p.CommandText = SqlitePragmaInterceptor.Pragmas; await p.ExecuteNonQueryAsync(ct); }

        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_meta'";
        var exists = Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
        if (exists)
        {
            await using var v = conn.CreateCommand();
            v.CommandText = "SELECT meta_value FROM app_meta WHERE meta_key='schema_version'";
            var version = int.TryParse(Convert.ToString(await v.ExecuteScalarAsync(ct)), out var n) ? n : 0;
            if (version > CurrentSchemaVersion)
                throw new InvalidOperationException($"Database schema v{version} is newer than this app (v{CurrentSchemaVersion}). Update the application.");
            // Future: apply incremental migrations here (v1 → v2 …), each inside a transaction.
            return;
        }

        log.LogInformation("Creating local database (schema v{Version})", CurrentSchemaVersion);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var script in new[] { "sqlite-schema.sql", "sqlite-seed.sql" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = ReadResource(script).Replace("PRAGMA foreign_keys=ON;", "");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // A fresh install gets its own provisional device id until it is registered with the server.
        await using (var dev = conn.CreateCommand())
        {
            dev.Transaction = tx;
            dev.CommandText = "INSERT OR IGNORE INTO sync_state (state_key, state_value, updated_at) VALUES ($k, $v, $t)";
            dev.Parameters.AddWithValue("$k", SyncStateKeys.DeviceId);
            dev.Parameters.AddWithValue("$v", Guid.NewGuid().ToString());
            dev.Parameters.AddWithValue("$t", UtcText.Write(DateTime.UtcNow));
            await dev.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
