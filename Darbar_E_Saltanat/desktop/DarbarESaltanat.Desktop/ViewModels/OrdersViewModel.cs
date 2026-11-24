using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Domain.Security;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed record OrderRow(OrderSummary S)
{
    public string OrderNo => S.OrderNo;
    public string Time => Dhaka.ToLocal(S.OrderDateUtc).ToString("dd MMM, h:mm tt");
    public string Type => OrderType.Label(S.OrderType);
    public string Table => S.TableName ?? "—";
    public string Customer => S.CustomerName ?? "—";
    public string Status => S.Status[0] + S.Status[1..].ToLowerInvariant().Replace('_', ' ');
    public string Payment => S.PaymentStatus[0] + S.PaymentStatus[1..].ToLowerInvariant().Replace('_', ' ');
    public string Total => Money.Format(S.Total);
    public int Items => S.ItemCount;
}

public sealed partial class OrdersViewModel(OrderService orders, IPrintService printer, DialogService dialogs, Navigator nav, ICurrentSession session)
    : ObservableObject, IPage
{
    public ObservableCollection<OrderRow> Rows { get; } = [];
    public string[] Periods { get; } = ["Today", "Yesterday", "Last 7 days", "Last 30 days"];
    public string[] Statuses { get; } = ["All", OrderStatus.Open, OrderStatus.InKitchen, OrderStatus.Held, OrderStatus.Billed, OrderStatus.Completed, OrderStatus.Voided, OrderStatus.Cancelled];

    [ObservableProperty] private string _period = "Today";
    [ObservableProperty] private string _status = "All";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private OrderRow? _selected;
    [ObservableProperty] private OrderView? _detail;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _totals = "";

    public bool CanRefund => session.Has(Permissions.PaymentsRefund);
    public bool CanVoid => session.Has(Permissions.OrdersVoid);
    public bool CanReprint => session.Has(Permissions.OrdersReprint);
    public string DetailText => Detail is null ? "Select an order to see its items and payments." : string.Join(Environment.NewLine,
        Detail.Lines.Select(l => $"{(l.IsCancelled ? "✗ " : "")}{l.Quantity} × {l.Name}{(l.AddonsText is null ? "" : $" (+{l.AddonsText})")}   {(l.IsCancelled ? "cancelled" : Money.Format(l.LineTotal))}")
        .Concat(new[]
        {
            "",
            $"Subtotal {Money.Format(Detail.Order.Subtotal)} · Discount {Money.Format(Detail.Order.DiscountAmount)} · Service {Money.Format(Detail.Order.ServiceChargeAmount)} · VAT {Money.Format(Detail.Order.VatAmount)}",
            $"TOTAL {Money.Format(Detail.Order.Total)} · Paid {Money.Format(Detail.NetPaid)} · Due {Money.Format(Detail.Order.DueAmount)}",
        })
        .Concat(Detail.Payments.Select(p => $"Payment: {PaymentMethod.Label(p.Method)} {Money.Format(p.Amount)} at {Dhaka.ToLocal(p.PaidAt):h:mm tt}{(p.Reference is null ? "" : $" ref {p.Reference}")}"))
        .Concat(Detail.Refunds.Select(r => $"Refund: {PaymentMethod.Label(r.Method)} {Money.Format(r.Amount)} — {r.Reason}"))
        .Concat(Detail.Order.VoidReason is null ? Array.Empty<string>() : new[] { $"Void/cancel reason: {Detail.Order.VoidReason}" }));

    partial void OnPeriodChanged(string value) => _ = RefreshAsync();
    partial void OnStatusChanged(string value) => _ = RefreshAsync();
    partial void OnDetailChanged(OrderView? value) => OnPropertyChanged(nameof(DetailText));
    async partial void OnSelectedChanged(OrderRow? value) => Detail = value is null ? null : await orders.GetAsync(value.S.Uuid);

    public Task LoadAsync(object? argument) => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var today = Dhaka.StartOfDayUtc(DateTime.UtcNow);
        var (from, to) = Period switch
        {
            "Yesterday" => (today.AddDays(-1), today),
            "Last 7 days" => (today.AddDays(-6), today.AddDays(1)),
            "Last 30 days" => (today.AddDays(-29), today.AddDays(1)),
            _ => (today, today.AddDays(1)),
        };
        var list = await orders.ListAsync(from, to, Status == "All" ? null : Status, Search);
        Rows.Clear();
        foreach (var o in list) Rows.Add(new OrderRow(o));
        var done = list.Where(o => o.Status == OrderStatus.Completed).ToList();
        Totals = $"{list.Count} orders · {done.Count} completed · {Money.Format(done.Sum(o => o.Total))} sales";
    }

    [RelayCommand] private void OpenInPos() { if (Selected is { } r) nav.Go(PageKeys.Pos, r.S.Uuid); }

    [RelayCommand]
    private async Task PrintVoucherAsync()
    {
        if (Selected is not { } r) return;
        await HandleAsync(await orders.ReprintAsync(r.S.Uuid));
    }

    [RelayCommand]
    private async Task ReprintAsync()
    {
        if (Selected is not { } r) return;
        await HandleAsync(await orders.ReprintAsync(r.S.Uuid));
    }

    [RelayCommand]
    private async Task RefundAsync()
    {
        if (Detail is not { } v) return;
        if (v.NetPaid <= 0) { Message = "Nothing has been paid on this order."; return; }
        var amountText = dialogs.Prompt($"Refund {v.Order.OrderNo}", $"Amount to refund (max {Money.Format(v.NetPaid)})", v.NetPaid.ToString("0.00"),
            validate: s => decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var a) && a > 0 && a <= v.NetPaid ? null : $"Enter an amount up to {v.NetPaid:0.00}");
        if (amountText is null) return;
        var method = dialogs.Choose("Refund method", PaymentMethod.All.Where(m => m != PaymentMethod.Other).Select(m => new MethodOption(m)).ToList(), m => m.Label);
        if (method is null) return;
        var reason = dialogs.Prompt("Refund reason", "Why is this being refunded?", validate: s => s.Length >= 3 ? null : "Enter a reason (at least 3 characters)");
        if (reason is null) return;
        var amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        if (!dialogs.Confirm($"Refund {Money.Format(amount)} by {method.Label} for {v.Order.OrderNo}?")) return;
        await HandleAsync(await orders.RefundAsync(v.Order.Uuid, amount, method.Method, reason));
    }

    public sealed record MethodOption(string Method) { public string Label => PaymentMethod.Label(Method); }

    [RelayCommand]
    private async Task VoidAsync()
    {
        if (Detail is not { } v) return;
        var reason = dialogs.Prompt($"Void {v.Order.OrderNo}", "Reason for voiding", validate: s => s.Length >= 3 ? null : "Enter a reason (at least 3 characters)");
        if (reason is null || !dialogs.Confirm($"Void {v.Order.OrderNo}? This cannot be undone.")) return;
        await HandleAsync(await orders.VoidAsync(v.Order.Uuid, reason));
    }

    private async Task HandleAsync(Result<PosOutcome> r)
    {
        if (!r.Succeeded) { Message = r.Error; return; }
        var problems = new List<string>();
        foreach (var job in r.Value!.PrintJobs) if (await printer.PrintAsync(job) is { } e) problems.Add(e);
        if (r.Value.OpenDrawer && await printer.OpenCashDrawerAsync() is { } d) problems.Add(d);
        Message = problems.Count > 0 ? string.Join(Environment.NewLine, problems) : r.Value.Notice;
        Detail = r.Value.View;
        await RefreshAsync();
    }
}
