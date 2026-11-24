using System.Net.NetworkInformation;
using DarbarESaltanat.Application.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DarbarESaltanat.Infrastructure.Sync;

/// <summary>
/// Background sync loop: shortly after every local save (the cloud database follows the desktop within seconds),
/// every <c>sync_interval_seconds</c> (default 30 s) while online to pull changes made elsewhere (web portal, other
/// counters), immediately when the network comes back or the user presses "Sync now", and with exponential back-off
/// (30 s → 15 min) after failures.
/// </summary>
public sealed class SyncWorker(SyncEngine engine, ISyncStore store, ILogger<SyncWorker> log) : BackgroundService
{
    /// <summary>
    /// After a trigger, wait this long before syncing so a burst of saves (order + items + KOT + payment) goes up as one
    /// batch, and a busy counter makes at most a few requests per minute (the API allows 300 per minute per IP).
    /// </summary>
    private static readonly TimeSpan TriggerSettleDelay = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _trigger = new(0, 1);

    /// <summary>True while sync is not configured or failing: saving data must not wake the worker then (it would hammer the server).</summary>
    private volatile bool _paused;

    /// <summary>Run a sync as soon as possible (Sync now button, network reconnect, settings saved).</summary>
    public void TriggerNow()
    {
        try { if (_trigger.CurrentCount == 0) _trigger.Release(); }
        catch (SemaphoreFullException) { /* already triggered */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        SyncSignal.LocalChangesCommitted += OnLocalChangesCommitted;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); // let the UI start first
            var failures = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                SyncRunResult result;
                try { result = await engine.RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogError(ex, "Sync loop error"); result = new SyncRunResult(SyncOutcome.Error, Error: ex.Message); }

                failures = result.Outcome is SyncOutcome.Offline or SyncOutcome.Error or SyncOutcome.Unauthorized ? failures + 1 : 0;
                var cfg = await store.GetConfigAsync(stoppingToken);
                var delay = failures > 0 ? SyncBackoff.ForRun(failures) : TimeSpan.FromSeconds(cfg.IntervalSeconds);
                if (result.Outcome == SyncOutcome.NotConfigured) delay = TimeSpan.FromMinutes(1);
                _paused = failures > 0 || result.Outcome == SyncOutcome.NotConfigured;
                try
                {
                    var triggered = await _trigger.WaitAsync(delay, stoppingToken);
                    if (triggered) await Task.Delay(TriggerSettleDelay, stoppingToken); // let a burst of saves finish, then sync once
                }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            SyncSignal.LocalChangesCommitted -= OnLocalChangesCommitted;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        }
    }

    /// <summary>A save just committed outbox rows: push them now, unless sync is unconfigured or backing off after a failure.</summary>
    private void OnLocalChangesCommitted()
    {
        if (!_paused) TriggerNow();
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        log.LogInformation("Network available again — syncing now");
        TriggerNow();
    }
}
