using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Sync;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Infrastructure.Sync;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed record LogRow(SyncLogLine L)
{
    public string Time => Dhaka.ToLocal(L.OccurredAtUtc).ToString("dd MMM h:mm:ss tt");
    public string Level => L.Level;
    public string Message => L.Message;
    public string? Details => L.Details;
}

public sealed record ProblemRow(OutboxProblem P)
{
    public string What => $"{P.EntityType} · {P.Operation}";
    public string Record => P.EntityUuid[..8];
    public string Status => P.Status;
    public string Error => P.Error ?? "";
    public string Retry => P.Status == "CONFLICT" ? "Resolve in web portal" : P.NextAttemptUtc is { } t ? $"retry #{P.RetryCount + 1} at {Dhaka.ToLocal(t):h:mm tt}" : "";
}

/// <summary>Desktop sync dashboard: health, counts, problem changes and the sync log.</summary>
public sealed partial class SyncViewModel(SyncStatusMonitor monitor, SyncStatus status, ISyncStore store, SyncWorker worker, IClock clock)
    : ObservableObject, IPage, IDisposable
{
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    public SyncStatusMonitor Monitor => monitor;
    public ObservableCollection<ProblemRow> Problems { get; } = [];
    public ObservableCollection<LogRow> Log { get; } = [];
    [ObservableProperty] private string _lastAttempt = "—";
    [ObservableProperty] private string _lastSuccess = "—";
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private string? _message;

    public async Task LoadAsync(object? argument)
    {
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await monitor.RefreshAsync();
        LastAttempt = status.LastAttemptUtc is { } a ? Dhaka.ToLocal(a).ToString("dd MMM, h:mm:ss tt") : "—";
        LastSuccess = status.LastSuccessUtc is { } s ? Dhaka.ToLocal(s).ToString("dd MMM, h:mm:ss tt") : "—";
        LastError = status.LastError;
        Problems.Clear();
        foreach (var p in await store.ProblemsAsync(100)) Problems.Add(new ProblemRow(p));
        Log.Clear();
        foreach (var l in await store.RecentLogAsync(100)) Log.Add(new LogRow(l));
    }

    [RelayCommand]
    private void SyncNow()
    {
        worker.TriggerNow();
        Message = "Sync started…";
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        var n = await store.RetryFailedAsync(clock.UtcNow);
        worker.TriggerNow();
        Message = n == 0 ? "Nothing to retry." : $"{n} change(s) will be retried now.";
        await RefreshAsync();
    }

    public void Dispose() => _timer.Stop();
}
