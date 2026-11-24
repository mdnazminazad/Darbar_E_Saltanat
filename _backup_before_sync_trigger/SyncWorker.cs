using System.Net.NetworkInformation;
using DarbarESaltanat.Application.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DarbarESaltanat.Infrastructure.Sync;

/// <summary>
/// Background sync loop: every <c>sync_interval_seconds</c> (default 30 s) while online, immediately when the
/// network comes back or the user presses "Sync now", and with exponential back-off (30 s → 15 min) after failures.
/// </summary>
public sealed class SyncWorker(SyncEngine engine, ISyncStore store, ILogger<SyncWorker> log) : BackgroundService
{
    private readonly SemaphoreSlim _trigger = new(0, 1);

    /// <summary>Run a sync as soon as possible (Sync now button, network reconnect, settings saved).</summary>
    public void TriggerNow()
    {
        try { if (_trigger.CurrentCount == 0) _trigger.Release(); }
        catch (SemaphoreFullException) { /* already triggered */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
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
                try { await _trigger.WaitAsync(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally { NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged; }
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        log.LogInformation("Network available again — syncing now");
        TriggerNow();
    }
}
