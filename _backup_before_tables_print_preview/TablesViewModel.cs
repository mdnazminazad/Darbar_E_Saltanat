using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Application.Tables;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Domain.Security;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed record TableCard(TableTile Tile)
{
    public string Name => Tile.Table.Name;
    public string Status => Tile.Orders.Count > 0 && Tile.Table.Status is TableStatus.Available or TableStatus.Reserved ? TableStatus.Occupied : Tile.Table.Status;
    public string StatusLabel => Status[0] + Status[1..].ToLowerInvariant();
    public string Seats => $"{Tile.Table.Capacity} seats{(Tile.Table.Section is null ? "" : " · " + Tile.Table.Section)}";
    public string? OrderLine => Tile.Primary is { } o
        ? $"{o.OrderNo[(o.OrderNo.LastIndexOf('-') + 1)..]} · {Money.Format(Tile.Orders.Sum(x => x.Total))} · {o.GuestCount} guests{(Tile.Orders.Count > 1 ? $" · {Tile.Orders.Count} bills" : "")}"
        : null;
    public string? Elapsed => Tile.Primary is { } o ? $"{(int)(DateTime.UtcNow - o.OpenedAtUtc).TotalMinutes} min" : null;
}

public sealed partial class TablesViewModel(TableService tables, OrderService orders, DialogService dialogs, Navigator nav, ICurrentSession session)
    : ObservableObject, IPage, IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    public ObservableCollection<TableCard> Cards { get; } = [];
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _summary = "";
    public bool CanManage => session.Has(Permissions.TablesManage);

    public async Task LoadAsync(object? argument)
    {
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var board = await tables.GetBoardAsync();
        Cards.Clear();
        foreach (var t in board) Cards.Add(new TableCard(t));
        Summary = $"{Cards.Count(c => c.Status == TableStatus.Available)} available · {Cards.Count(c => c.Status is TableStatus.Occupied or TableStatus.Billing)} in use · {Cards.Count(c => c.Status == TableStatus.Reserved)} reserved · {Cards.Count(c => c.Status == TableStatus.Cleaning)} cleaning";
    }

    /// <summary>Tap a table: open its order in the POS, or start a new dine-in order.</summary>
    [RelayCommand]
    private async Task OpenAsync(TableCard? card)
    {
        if (card is null) return;
        if (card.Tile.Orders.Count > 0)
        {
            var target = card.Tile.Orders.Count == 1 ? card.Tile.Orders[0]
                : dialogs.Choose($"Table {card.Name} — choose bill", card.Tile.Orders, o => o.OrderNo, o => Money.Format(o.Total));
            if (target is not null) nav.Go(PageKeys.Pos, target.OrderUuid);
            return;
        }
        if (card.Status == TableStatus.Cleaning)
        {
            if (!dialogs.Confirm($"Table {card.Name} is being cleaned. Mark it available and seat guests?")) return;
            var r = await tables.SetStatusAsync(card.Tile.Table.Uuid, TableStatus.Available);
            if (!r.Succeeded) { Message = r.Error; return; }
        }
        var guests = dialogs.Prompt($"Table {card.Name}", "Number of guests", Math.Min(2, card.Tile.Table.Capacity).ToString(),
            validate: s => int.TryParse(s, out var g) && g is >= 1 and <= 100 ? null : "Enter 1–100");
        if (guests is null) return;
        var created = await orders.CreateAsync(new CreateOrderRequest(OrderType.DineIn, card.Tile.Table.Uuid, GuestCount: int.Parse(guests)));
        if (!created.Succeeded) { Message = created.Error; await RefreshAsync(); return; }
        nav.Go(PageKeys.Pos, created.Value!.View.Order.Uuid);
    }

    [RelayCommand]
    private async Task SetStatusAsync(TableCard? card)
    {
        if (card is null) return;
        var options = new[] { TableStatus.Available, TableStatus.Reserved, TableStatus.Cleaning }.Select(s => new StatusOption(s)).ToList();
        var pick = dialogs.Choose($"Table {card.Name} status", options, o => o.Label);
        if (pick is null) return;
        var r = await tables.SetStatusAsync(card.Tile.Table.Uuid, pick.Status);
        Message = r.Succeeded ? null : r.Error;
        await RefreshAsync();
    }

    public sealed record StatusOption(string Status) { public string Label => Status[0] + Status[1..].ToLowerInvariant(); }

    [RelayCommand]
    private async Task AddTableAsync()
    {
        var name = dialogs.Prompt("New table", "Table name (e.g. T11, F4, V3)", validate: s => s.Length is >= 1 and <= 30 ? null : "1–30 characters");
        if (name is null) return;
        var cap = dialogs.Prompt("New table", "Seats", "4", validate: s => int.TryParse(s, out var c) && c is >= 1 and <= 100 ? null : "Enter 1–100");
        if (cap is null) return;
        var section = dialogs.Prompt("New table", "Section (optional, e.g. Main Hall, Family Cabin, VIP Room)");
        if (section is null) return;
        var r = await tables.SaveAsync(new TableInput(null, name, int.Parse(cap), section));
        Message = r.Succeeded ? $"Table {name} added" : r.Error;
        await RefreshAsync();
    }

    public void Dispose() => _timer.Stop();
}
