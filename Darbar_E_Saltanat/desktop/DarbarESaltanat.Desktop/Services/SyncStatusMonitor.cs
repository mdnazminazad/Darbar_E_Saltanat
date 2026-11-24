using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Sync;
using DarbarESaltanat.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DarbarESaltanat.Desktop.Services;

/// <summary>
/// Status-bar view of sync: combines the engine's live state (offline, syncing, unauthorised…) with outbox counts
/// read from LOCAL SQLite every few seconds — so a change made a moment ago shows as "pending" immediately.
/// </summary>
public sealed partial class SyncStatusMonitor : ObservableObject
{
    private readonly ISyncStore _store;
    private readonly SyncStatus _status;
    private readonly ILogger<SyncStatusMonitor> _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    [ObservableProperty] private SyncHealth _health = SyncHealth.NotConfigured;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private string _label = "Sync not configured";
    [ObservableProperty] private string _lastSync = "Never";

    public SyncStatusMonitor(ISyncStore store, SyncStatus status, ILogger<SyncStatusMonitor> log)
    {
        _store = store; _status = status; _log = log;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _status.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () => await RefreshAsync());
    }

    public void Start() { _timer.Start(); _ = RefreshAsync(); }

    public async Task RefreshAsync()
    {
        try
        {
            var c = await _store.CountsAsync();
            PendingCount = c.Pending + c.Syncing;
            ConflictCount = c.Conflicts;
            FailedCount = c.Failed;
            var engine = _status.Health;
            Health = engine is SyncHealth.Offline or SyncHealth.Unauthorized or SyncHealth.NotConfigured or SyncHealth.Syncing
                ? (engine == SyncHealth.NotConfigured || engine == SyncHealth.Syncing || c.Conflicts == 0 ? engine : SyncHealth.Conflicts)
                : SyncStatus.FromCounts(c);
            LastSync = _status.LastSuccessUtc is { } t ? Dhaka.ToLocal(t).ToString("h:mm:ss tt") : "Never";
            Label = Health switch
            {
                SyncHealth.NotConfigured => $"Offline mode · {PendingCount} change(s) saved locally (sync not configured)",
                SyncHealth.Syncing => "Syncing…",
                SyncHealth.Offline => $"Offline · {PendingCount} change(s) waiting · last sync {LastSync}",
                SyncHealth.Unauthorized => "Sync stopped: device not authorised (Settings → Server sync)",
                SyncHealth.Conflicts => $"Conflicts found: {ConflictCount} — an admin resolves them in the web portal",
                SyncHealth.Failed => $"Sync failed for {FailedCount} change(s) — see Sync page",
                SyncHealth.PendingChanges => $"Online · {PendingCount} pending change(s)",
                _ => $"Online · all changes synced · {LastSync}",
            };
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read sync status"); }
    }
}
