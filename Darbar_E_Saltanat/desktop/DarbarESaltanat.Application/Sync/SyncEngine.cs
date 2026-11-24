using System.Text.Json;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DarbarESaltanat.Application.Sync;

public enum SyncOutcome { NotConfigured, AlreadyRunning, Completed, Offline, Unauthorized, Error }

public sealed record SyncRunResult(SyncOutcome Outcome, int Pushed = 0, int Conflicts = 0, int Failed = 0, int Pulled = 0, string? Error = null)
{
    public static readonly SyncRunResult AlreadyRunning = new(SyncOutcome.AlreadyRunning);
}

/// <summary>Live sync state shown in the status bar and on the Sync page.</summary>
public sealed class SyncStatus
{
    private readonly object _gate = new();
    public SyncHealth Health { get; private set; } = SyncHealth.NotConfigured;
    public bool IsRunning { get; private set; }
    public DateTime? LastAttemptUtc { get; private set; }
    public DateTime? LastSuccessUtc { get; private set; }
    public string? LastError { get; private set; }
    public SyncCounts Counts { get; private set; } = new(0, 0, 0, 0);
    public event EventHandler? Changed;

    public void Update(SyncHealth health, SyncCounts? counts = null, string? error = null, bool? running = null, DateTime? attempt = null, DateTime? success = null)
    {
        lock (_gate)
        {
            Health = health;
            if (counts is not null) Counts = counts;
            LastError = error;
            if (running is not null) IsRunning = running.Value;
            if (attempt is not null) LastAttemptUtc = attempt;
            if (success is not null) LastSuccessUtc = success;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static SyncHealth FromCounts(SyncCounts c) =>
        c.Conflicts > 0 ? SyncHealth.Conflicts : c.Failed > 0 ? SyncHealth.Failed : c.Pending + c.Syncing > 0 ? SyncHealth.PendingChanges : SyncHealth.Synced;
}

/// <summary>
/// One sync run: push the outbox in batches, then pull server changes page by page, then acknowledge + heartbeat.
/// Never holds a database transaction across a network call: the store claims a batch (short transaction),
/// the HTTP call happens with no transaction open, then the results are applied (another short transaction).
/// </summary>
public sealed class SyncEngine(ISyncApi api, ISyncStore store, IClock clock, SyncStatus status, ILogger<SyncEngine> log)
{
    public const int MaxBatchesPerRun = 20;
    public const int MaxPullPages = 200;
    public const int PullPageSize = 500;
    public static string AppVersion { get; set; } = "1.0.0";

    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Raised after pulled rows were written locally (argument: affected table names).</summary>
    public event EventHandler<IReadOnlyCollection<string>>? RemoteChangesApplied;

    public async Task<SyncRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (!await _running.WaitAsync(0, ct)) return SyncRunResult.AlreadyRunning;
        try { return await RunCoreAsync(ct); }
        finally { _running.Release(); }
    }

    private async Task<SyncRunResult> RunCoreAsync(CancellationToken ct)
    {
        var cfg = await store.GetConfigAsync(ct);
        if (!cfg.IsConfigured)
        {
            status.Update(SyncHealth.NotConfigured, await store.CountsAsync(ct), running: false);
            return new SyncRunResult(SyncOutcome.NotConfigured);
        }
        var ep = cfg.Endpoint;
        var started = clock.UtcNow;
        status.Update(SyncHealth.Syncing, running: true, attempt: started);
        int pushed = 0, conflicts = 0, failed = 0, pulled = 0;
        try
        {
            // ---------------------------------------------------------------- push
            for (var i = 0; i < MaxBatchesPerRun; i++)
            {
                var batch = await store.ClaimBatchAsync(cfg.BatchSize, clock.UtcNow, ct);
                if (batch is null) break;
                var request = new PushRequest(batch.BatchId, cfg.DeviceId, batch.Entries.Select(ToChange).ToList());
                PushResponse response;
                try { response = await api.PushAsync(ep, request, ct); }
                catch (SyncRejectedException ex)
                {
                    await store.RejectBatchAsync(batch, ex.Message, clock.UtcNow, ct);
                    await store.LogAsync("ERROR", "PUSH", batch.BatchId, "Server rejected the batch", ex.Message, ct);
                    failed += batch.Entries.Count;
                    continue;
                }
                catch (Exception ex) when (ex is SyncOfflineException or SyncAuthException or OperationCanceledException)
                {
                    await store.MarkBatchErrorAsync(batch, ex.Message, clock.UtcNow, CancellationToken.None);
                    throw;
                }
                if (response.Results.Count != batch.Entries.Count)
                {
                    await store.RejectBatchAsync(batch, $"Server returned {response.Results.Count} results for {batch.Entries.Count} changes", clock.UtcNow, ct);
                    failed += batch.Entries.Count;
                    continue;
                }
                await store.ApplyPushResultsAsync(batch, response.Results, clock.UtcNow, ct);
                var ok = response.Results.Count(r => r.Status == "SYNCED");
                var conf = response.Results.Count(r => r.Status == "CONFLICT");
                var bad = response.Results.Count - ok - conf;
                pushed += ok; conflicts += conf; failed += bad;
                await store.LogAsync(conf + bad > 0 ? "WARN" : "INFO", "PUSH", batch.BatchId,
                    $"Pushed {batch.Entries.Count} change(s): {ok} synced, {conf} conflict(s), {bad} failed{(batch.Resumed ? " (resumed batch)" : "")}{(response.Replayed ? " (server replay)" : "")}",
                    conf + bad > 0 ? string.Join("; ", response.Results.Where(r => r.Status != "SYNCED").Take(10).Select(r => $"{r.Entity} {r.Uuid[..8]}: {r.Status} {r.Reason}")) : null, ct);
            }

            // ---------------------------------------------------------------- pull
            string? cursor = null, nextSince = null;
            var touched = new HashSet<string>();
            for (var page = 0; page < MaxPullPages; page++)
            {
                var resp = await api.PullAsync(ep, cfg.LastPullSince, cursor, PullPageSize, ct);
                var applied = await store.ApplyPullAsync(resp.Changes, clock.UtcNow, ct);
                pulled += applied.Applied;
                foreach (var e in applied.Entities) touched.Add(e);
                if (resp.HasMore && resp.Cursor is not null) { cursor = resp.Cursor; continue; }
                nextSince = resp.NextSince;
                break;
            }
            if (nextSince is not null) await store.SetLastPullAsync(nextSince, ct);
            if (pulled > 0)
            {
                await store.LogAsync("INFO", "PULL", null, $"Applied {pulled} change(s) from the server", string.Join(", ", touched), ct);
                RemoteChangesApplied?.Invoke(this, touched);
            }

            var counts = await store.CountsAsync(ct);
            if (nextSince is not null) await api.AcknowledgeAsync(ep, nextSince, counts.Unsynced, ct);
            await api.HeartbeatAsync(ep, counts.Unsynced, AppVersion, ct);
            await store.HousekeepAsync(clock.UtcNow, ct);
            status.Update(SyncStatus.FromCounts(counts), counts, running: false, success: clock.UtcNow);
            return new SyncRunResult(SyncOutcome.Completed, pushed, conflicts, failed, pulled);
        }
        catch (SyncOfflineException ex)
        {
            status.Update(SyncHealth.Offline, await store.CountsAsync(CancellationToken.None), ex.Message, running: false);
            await store.LogAsync("WARN", "SYSTEM", null, "Server unreachable — working offline", ex.Message, CancellationToken.None);
            return new SyncRunResult(SyncOutcome.Offline, pushed, conflicts, failed, pulled, ex.Message);
        }
        catch (SyncAuthException ex)
        {
            status.Update(SyncHealth.Unauthorized, await store.CountsAsync(CancellationToken.None), ex.Message, running: false);
            await store.LogAsync("ERROR", "SYSTEM", null, "Device not authorised — check Settings → Server sync", ex.Message, CancellationToken.None);
            return new SyncRunResult(SyncOutcome.Unauthorized, Error: ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status.Update(SyncStatus.FromCounts(await store.CountsAsync(CancellationToken.None)), running: false);
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Sync run failed");
            status.Update(SyncHealth.Failed, await store.CountsAsync(CancellationToken.None), ex.Message, running: false);
            await store.LogAsync("ERROR", "SYSTEM", null, "Sync failed", ex.ToString(), CancellationToken.None);
            return new SyncRunResult(SyncOutcome.Error, pushed, conflicts, failed, pulled, ex.Message);
        }
    }

    private static PushChange ToChange(OutboxEntry e)
    {
        using var doc = JsonDocument.Parse(e.PayloadJson);
        var data = doc.RootElement.Clone();
        var updatedAt = data.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        return new PushChange(e.EntityType, e.Operation, e.EntityUuid, e.BaseVersion, updatedAt, data);
    }

    /// <summary>Checks the URL/token and returns the device's identity and branch (Settings → Server sync → Save).</summary>
    public Task<DeviceInfo> TestConnectionAsync(SyncEndpoint ep, CancellationToken ct = default) => api.GetDeviceAsync(ep, ct);
}
