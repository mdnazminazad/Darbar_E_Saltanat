using System.Text.Json;

namespace DarbarESaltanat.Application.Sync;

// ---- wire DTOs (JSON is snake_case; see docs/api.md and database/sync-design.md)

public sealed record PushChange(string Entity, string Operation, string Uuid, int SyncVersion, string? UpdatedAt, JsonElement Data);
public sealed record PushRequest(string BatchId, string DeviceId, IReadOnlyList<PushChange> Changes);
public sealed record PushResult(string Entity, string Uuid, string Status, int? ServerVersion = null, string? Reason = null, bool Retryable = false);
public sealed record PushResponse(string BatchId, string? ProcessedAt, IReadOnlyList<PushResult> Results, bool Replayed = false);
public sealed record PullEntity(string Entity, IReadOnlyList<JsonElement> Rows);
public sealed record PullResponse(string? ServerTime, bool HasMore, string? Cursor, string? NextSince, IReadOnlyList<PullEntity> Changes);
public sealed record DeviceInfo(string DeviceId, string? Name, string? Status, string? BranchUuid, string? BranchName, string? ServerTime);

public sealed record SyncEndpoint(string BaseUrl, string DeviceId, string DeviceToken);

// ---- local store DTOs

public sealed record SyncConfig(string? ApiBaseUrl, string DeviceId, string? DeviceToken, string? LastPullSince, int BatchSize = 100, int IntervalSeconds = 30)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiBaseUrl) && !string.IsNullOrWhiteSpace(DeviceToken);
    public SyncEndpoint Endpoint => new(ApiBaseUrl!.TrimEnd('/'), DeviceId, DeviceToken!);
}

public sealed record OutboxEntry(long Id, string EntityType, string EntityUuid, string Operation, string PayloadJson, int BaseVersion, int RetryCount);
public sealed record OutboxBatch(string BatchId, IReadOnlyList<OutboxEntry> Entries, bool Resumed);
public sealed record PullApplyResult(int Applied, int Skipped, IReadOnlyCollection<string> Entities);
public sealed record SyncCounts(int Pending, int Syncing, int Failed, int Conflicts)
{
    public int Unsynced => Pending + Syncing + Failed + Conflicts;
}
public sealed record SyncLogLine(DateTime OccurredAtUtc, string Level, string Direction, string? BatchId, string Message, string? Details);
public sealed record OutboxProblem(long Id, string EntityType, string EntityUuid, string Operation, string Status, int RetryCount, string? Error, DateTime? NextAttemptUtc);

// ---- abstractions

public interface ISyncApi
{
    Task<PushResponse> PushAsync(SyncEndpoint ep, PushRequest request, CancellationToken ct = default);
    Task<PullResponse> PullAsync(SyncEndpoint ep, string? since, string? cursor, int limit, CancellationToken ct = default);
    Task AcknowledgeAsync(SyncEndpoint ep, string pulledUntil, int pendingCount, CancellationToken ct = default);
    Task HeartbeatAsync(SyncEndpoint ep, int pendingCount, string appVersion, CancellationToken ct = default);
    Task<DeviceInfo> GetDeviceAsync(SyncEndpoint ep, CancellationToken ct = default);
}

/// <summary>Local sync bookkeeping (sync_outbox, sync_state, sync_log, applying pulled rows). Implemented over SQLite.</summary>
public interface ISyncStore
{
    Task<SyncConfig> GetConfigAsync(CancellationToken ct = default);
    /// <summary>Resumes an interrupted (SYNCING) batch first, so a retry re-sends the exact same batch_id.</summary>
    Task<OutboxBatch?> ClaimBatchAsync(int max, DateTime nowUtc, CancellationToken ct = default);
    Task ApplyPushResultsAsync(OutboxBatch batch, IReadOnlyList<PushResult> results, DateTime nowUtc, CancellationToken ct = default);
    /// <summary>Network failure: the batch stays SYNCING with the same batch_id and is replayed next run.</summary>
    Task MarkBatchErrorAsync(OutboxBatch batch, string error, DateTime nowUtc, CancellationToken ct = default);
    /// <summary>The server refused the whole batch: entries go back to FAILED with back-off and a new batch next time.</summary>
    Task RejectBatchAsync(OutboxBatch batch, string error, DateTime nowUtc, CancellationToken ct = default);
    Task<PullApplyResult> ApplyPullAsync(IReadOnlyList<PullEntity> changes, DateTime nowUtc, CancellationToken ct = default);
    Task SetLastPullAsync(string since, CancellationToken ct = default);
    Task<SyncCounts> CountsAsync(CancellationToken ct = default);
    Task LogAsync(string level, string direction, string? batchId, string message, string? details = null, CancellationToken ct = default);
    Task<IReadOnlyList<SyncLogLine>> RecentLogAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxProblem>> ProblemsAsync(int count, CancellationToken ct = default);
    /// <summary>FAILED → PENDING now (manual "retry failed").</summary>
    Task<int> RetryFailedAsync(DateTime nowUtc, CancellationToken ct = default);
    Task HousekeepAsync(DateTime nowUtc, CancellationToken ct = default);
}

/// <summary>Network unreachable, timeout, 5xx, 429 or batch-in-progress: try again later.</summary>
public sealed class SyncOfflineException(string message, Exception? inner = null) : Exception(message, inner);
/// <summary>401/403: the device token is wrong or the device was revoked.</summary>
public sealed class SyncAuthException(string message) : Exception(message);
/// <summary>The server refused the request itself (400/422…): retrying unchanged will not help.</summary>
public sealed class SyncRejectedException(string message) : Exception(message);

public static class SyncBackoff
{
    /// <summary>Per change: 15 s, 30 s, 1 min … capped at 30 min.</summary>
    public static TimeSpan ForItem(int retryCount) => TimeSpan.FromSeconds(Math.Min(15 * Math.Pow(2, Math.Min(retryCount, 12)), 1800));

    /// <summary>Whole sync loop after consecutive failures: 30 s, 1, 2, 4, 8 min … capped at 15 min, ±20 % jitter.</summary>
    public static TimeSpan ForRun(int consecutiveFailures, Random? rng = null)
    {
        var seconds = Math.Min(30 * Math.Pow(2, Math.Max(0, consecutiveFailures - 1)), 900);
        var jitter = 0.8 + (rng ?? Random.Shared).NextDouble() * 0.4;
        return TimeSpan.FromSeconds(seconds * jitter);
    }
}
