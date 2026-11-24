using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Dashboard;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Desktop.Services;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed record Kpi(string Label, string Value, string? Hint = null);

/// <summary>Today's (or last 7/30 days') sales from local SQLite, plus terminal and sync health.</summary>
public sealed partial class DashboardViewModel(
    ICurrentSession session, IDeviceContext device, SyncStatusMonitor sync, SettingsService settings, DashboardService dashboard)
    : ObservableObject, IPage
{
    public ObservableCollection<Kpi> Kpis { get; } = [];
    public ObservableCollection<Kpi> Payments { get; } = [];
    public ObservableCollection<Kpi> TopItems { get; } = [];
    public string[] Periods { get; } = ["Today", "Last 7 days", "Last 30 days"];

    [ObservableProperty] private string _period = "Today";
    [ObservableProperty] private string _vatSummary = "";
    public string Greeting => $"Welcome, {session.Current?.FullName}";
    public string DeviceId => device.DeviceId;
    public string BranchUuid => device.BranchUuid;
    public SyncStatusMonitor Sync => sync;
    public string LocalTime => Dhaka.ToLocal(DateTime.UtcNow).ToString("dddd, d MMMM yyyy · h:mm tt");

    partial void OnPeriodChanged(string value) => _ = RefreshAsync();

    public async Task LoadAsync(object? argument)
    {
        var s = await settings.GetAsync();
        VatSummary = $"VAT {s.VatRate:0.##}% {(s.VatInclusive ? "(inclusive)" : "(exclusive)")} · Service charge {s.ServiceChargeRate:0.##}%{(s.ServiceChargeDineInOnly ? " on dine-in" : "")} · Receipt {s.ReceiptWidthMm} mm";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var p = Period switch { "Last 7 days" => DashboardService.Period.Last7Days, "Last 30 days" => DashboardService.Period.Last30Days, _ => DashboardService.Period.Today };
        var (from, to) = dashboard.Range(p);
        var st = await dashboard.GetAsync(from, to);
        Kpis.Clear();
        Kpis.Add(new Kpi("Net sales", Money.Format(st.NetSales), st.Refunds > 0 ? $"after {Money.Format(st.Refunds)} refunds" : null));
        Kpis.Add(new Kpi("Orders", st.CompletedOrders.ToString(), st.VoidedOrders > 0 ? $"{st.VoidedOrders} voided" : null));
        Kpis.Add(new Kpi("Average order", Money.Format(st.AverageOrderValue)));
        Kpis.Add(new Kpi("Open orders", st.OpenOrders.ToString(), $"{st.ActiveTables} tables in use"));
        Kpis.Add(new Kpi("Kitchen tickets pending", st.PendingKitchenTickets.ToString()));
        Kpis.Add(new Kpi("VAT collected", Money.Format(st.Vat), $"Service charge {Money.Format(st.ServiceCharge)} · Discounts {Money.Format(st.Discounts)}"));
        Payments.Clear();
        Payments.Add(new Kpi("Cash", Money.Format(st.Cash)));
        Payments.Add(new Kpi("Card", Money.Format(st.Card)));
        Payments.Add(new Kpi("Mobile banking", Money.Format(st.MobileBanking)));
        Payments.Add(new Kpi("Credit", Money.Format(st.Credit)));
        TopItems.Clear();
        foreach (var t in st.TopItems) TopItems.Add(new Kpi(t.Name, $"{t.Quantity} sold", Money.Format(t.Amount)));
        if (TopItems.Count == 0) TopItems.Add(new Kpi("No completed sales yet in this period", ""));
        OnPropertyChanged(nameof(LocalTime));
    }
}
