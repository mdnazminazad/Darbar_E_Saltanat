using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Customers;
using DarbarESaltanat.Application.Menu;
using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Application.Tables;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Desktop.Views;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Domain.Security;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed record CategoryChip(string? Uuid, string Name);
public sealed record VoucherOption(string Action, string Label, string? Hint);

public sealed record MenuTile(MenuItem Item, string PriceText, bool HasOptions)
{
    public string Name => Item.Name;
    public string? NameBn => Item.NameBn;
    public bool IsAvailable => Item.IsAvailable;
}

public sealed record CartLine(OrderLineView Line)
{
    public string Name => Line.Name;
    public string? Details => string.Join(" · ", new[] { Line.AddonsText is null ? null : "+ " + Line.AddonsText, Line.Notes is null ? null : "Note: " + Line.Notes, Line.CancelReason is null ? null : "Cancelled: " + Line.CancelReason }.Where(x => x is not null));
    public bool HasDetails => Details!.Length > 0;
    public int Quantity => Line.Quantity;
    public string Amount => Line.IsCancelled ? "—" : Money.Format(Line.LineTotal);
    public string StatusLabel => Line.Status switch
    {
        OrderItemStatus.New => "New",
        OrderItemStatus.Sent => "In kitchen",
        OrderItemStatus.Preparing => "Preparing",
        OrderItemStatus.Ready => "Ready",
        OrderItemStatus.Served => "Served",
        _ => "Cancelled",
    };
    public bool IsNew => Line.IsNew;
    public bool IsCancelled => Line.IsCancelled;
}

/// <summary>The cashier/waiter screen: menu grid, cart, and every order action. Reads/writes only local SQLite.</summary>
public sealed partial class PosViewModel(
    OrderService orders, MenuService menu, TableService tables, CustomerService customers, SettingsService settings,
    IPrintService printer, DialogService dialogs, ICurrentSession session) : ObservableObject, IPage
{
    private PosCatalog? _catalog;
    private RestaurantSettings _settings = new();

    public ObservableCollection<CategoryChip> Categories { get; } = [];
    public ObservableCollection<MenuTile> Tiles { get; } = [];
    public ObservableCollection<CartLine> Lines { get; } = [];
    public ObservableCollection<OrderSummary> ActiveOrders { get; } = [];

    [ObservableProperty] private CategoryChip? _selectedCategory;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private OrderView? _current;
    [ObservableProperty] private CartLine? _selectedLine;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _busy;

    public bool CanVoid => session.Has(Permissions.OrdersVoid);
    public bool CanTransfer => session.Has(Permissions.OrdersTransfer);
    public bool CanPay => session.Has(Permissions.PaymentsTake);
    public bool CanDiscount => session.Has(Permissions.DiscountsApply);

    // ---- current order display
    public bool HasOrder => Current is not null;
    public bool IsEditable => Current is { IsClosed: false };
    public string OrderTitle => Current is null ? "No order selected" : $"{Current.Order.OrderNo}";
    public string OrderSubtitle => Current is null ? "Tap an item to start a takeaway order, or choose New dine-in / delivery."
        : $"{OrderType.Label(Current.Order.OrderType)}{(Current.TableName is null ? "" : $" · Table {Current.TablesLabel}")} · {StatusText(Current.Order.Status)}"
          + (Current.Customer is null ? "" : $" · {Current.Customer.Name} ({Current.Customer.Phone})");
    public string SubtotalText => Money.Format(Current?.Order.Subtotal ?? 0);
    public bool ShowDiscount => Current?.Order.DiscountAmount > 0;
    public string DiscountLabel => Current?.Order.DiscountType switch
    {
        DiscountType.Percent => $"Discount ({Current.Order.DiscountValue:0.##}%)",
        DiscountType.Coupon => $"Coupon {Current.CouponCode}",
        _ => "Discount",
    };
    public string DiscountText => "−" + Money.Format(Current?.Order.DiscountAmount ?? 0);
    public bool ShowServiceCharge => Current?.Order.ServiceChargeAmount > 0;
    public string ServiceChargeLabel => $"Service charge ({Current?.Order.ServiceChargeRate:0.##}%)";
    public string ServiceChargeText => Money.Format(Current?.Order.ServiceChargeAmount ?? 0);
    public string VatLabel => _settings.VatInclusive ? "VAT (included)" : "VAT";
    public string VatText => Money.Format(Current?.Order.VatAmount ?? 0);
    public bool ShowDelivery => Current?.Order.DeliveryCharge > 0;
    public string DeliveryText => Money.Format(Current?.Order.DeliveryCharge ?? 0);
    public bool ShowRoundOff => Current is not null && Current.Order.RoundOff != 0;
    public string RoundOffText => (Current?.Order.RoundOff ?? 0).ToString("+0.00;−0.00");
    public string TotalText => Money.Format(Current?.Order.Total ?? 0);
    public bool ShowPaid => Current?.TotalPayments > 0;
    public string PaidText => Money.Format(Current?.NetPaid ?? 0);
    public string DueText => Money.Format(Current?.Order.DueAmount ?? 0);

    private static string StatusText(string s) => s switch
    {
        OrderStatus.InKitchen => "In kitchen",
        OrderStatus.Held => "On hold",
        OrderStatus.Billed => "Bill printed",
        _ => s[0] + s[1..].ToLowerInvariant(),
    };

    partial void OnCurrentChanged(OrderView? value)
    {
        Lines.Clear();
        if (value is not null) foreach (var l in value.Lines) Lines.Add(new CartLine(l));
        foreach (var p in new[]
        {
            nameof(HasOrder), nameof(IsEditable), nameof(OrderTitle), nameof(OrderSubtitle), nameof(SubtotalText), nameof(ShowDiscount),
            nameof(DiscountLabel), nameof(DiscountText), nameof(ShowServiceCharge), nameof(ServiceChargeLabel), nameof(ServiceChargeText),
            nameof(VatLabel), nameof(VatText), nameof(ShowDelivery), nameof(DeliveryText), nameof(ShowRoundOff), nameof(RoundOffText),
            nameof(TotalText), nameof(ShowPaid), nameof(PaidText), nameof(DueText),
        }) OnPropertyChanged(p);
    }

    partial void OnSelectedCategoryChanged(CategoryChip? value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // ------------------------------------------------------------------ loading

    public async Task LoadAsync(object? argument)
    {
        _settings = await settings.GetAsync();
        await ReloadCatalogAsync();
        await RefreshActiveAsync();
        if (argument is string orderUuid) Current = await orders.GetAsync(orderUuid);
    }

    [RelayCommand]
    private async Task ReloadCatalogAsync()
    {
        _catalog = await menu.GetCatalogAsync();
        Categories.Clear();
        Categories.Add(new CategoryChip(null, "All"));
        foreach (var c in _catalog.Categories) Categories.Add(new CategoryChip(c.Uuid, c.Name));
        SelectedCategory = Categories[0];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Tiles.Clear();
        if (_catalog is null) return;
        var q = SearchText.Trim();
        var items = _catalog.Items.AsEnumerable();
        if (q.Length > 0)
            items = items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || (i.NameBn?.Contains(q) ?? false)
                                     || string.Equals(i.Sku, q, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(i.Barcode, q, StringComparison.OrdinalIgnoreCase));
        else if (SelectedCategory?.Uuid is { } cat) items = items.Where(i => i.CategoryUuid == cat);
        foreach (var i in items)
        {
            var vs = _catalog.VariantsOf(i.Uuid).ToList();
            var price = vs.Count > 1 ? $"from {Money.Format(vs.Min(v => v.Price))}" : Money.Format(vs.Count == 1 ? vs[0].Price : i.SellingPrice);
            Tiles.Add(new MenuTile(i, price, _catalog.HasOptions(i.Uuid)));
        }
    }

    private async Task RefreshActiveAsync()
    {
        var list = await orders.ListActiveAsync();
        ActiveOrders.Clear();
        foreach (var o in list) ActiveOrders.Add(o);
    }

    // ------------------------------------------------------------------ action runner

    private void Show(string? message, bool error = false) { Message = message; IsError = error; }

    /// <summary>Runs a POS action, then prints whatever it produced. Printing never blocks or undoes the save.</summary>
    private async Task<bool> RunAsync(Func<Task<Result<PosOutcome>>> action)
    {
        if (Busy) return false;
        Busy = true;
        Show(null);
        try
        {
            var r = await action();
            if (!r.Succeeded) { Show(r.Error, true); return false; }
            var outcome = r.Value!;
            Current = outcome.View;
            var problems = new List<string>();
            foreach (var job in outcome.PrintJobs)
                if (await printer.PrintAsync(job) is { } err) problems.Add(err);
            if (outcome.OpenDrawer && await printer.OpenCashDrawerAsync() is { } drawerErr) problems.Add(drawerErr);
            Show(problems.Count > 0 ? string.Join(Environment.NewLine, problems) : outcome.Notice, problems.Count > 0);
            await RefreshActiveAsync();
            return true;
        }
        catch (PermissionDeniedException ex) { Show(ex.Message, true); return false; }
        finally { Busy = false; }
    }

    private string? CurrentUuid => Current?.Order.Uuid;

    private async Task<bool> EnsureOrderAsync()
    {
        if (Current is { IsClosed: false }) return true;
        return await RunAsync(() => orders.CreateAsync(new CreateOrderRequest(OrderType.Takeaway)));
    }

    // ------------------------------------------------------------------ new orders

    [RelayCommand]
    private async Task NewTakeawayAsync() => await RunAsync(() => orders.CreateAsync(new CreateOrderRequest(OrderType.Takeaway)));

    [RelayCommand]
    private async Task NewDineInAsync()
    {
        var board = await tables.GetBoardAsync();
        var pick = dialogs.Choose("Choose a table", board, t => $"{t.Table.Name} ({t.Table.Capacity})",
            t => t.Primary is { } o ? $"{o.OrderNo} · {Money.Format(o.Total)}" : t.Table.Status == TableStatus.Available ? t.Table.Section : t.Table.Status.ToLowerInvariant());
        if (pick is null) return;
        if (pick.Primary is { } existing) { Current = await orders.GetAsync(existing.OrderUuid); Show($"Table {pick.Table.Name} already has order {existing.OrderNo} — opened it."); return; }
        var guests = dialogs.Prompt($"Table {pick.Table.Name}", "Number of guests", Math.Min(pick.Table.Capacity, 2).ToString(),
            validate: s => int.TryParse(s, out var g) && g is >= 1 and <= 100 ? null : "Enter 1–100");
        if (guests is null) return;
        await RunAsync(() => orders.CreateAsync(new CreateOrderRequest(OrderType.DineIn, pick.Table.Uuid, GuestCount: int.Parse(guests))));
    }

    [RelayCommand]
    private async Task NewDeliveryAsync()
    {
        var phone = dialogs.Prompt("Delivery order", "Customer mobile number", validate: s => CustomerService.IsValidBdMobile(CustomerService.NormalizePhone(s)) ? null : "Enter a valid number (01XXXXXXXXX)");
        if (phone is null) return;
        var customer = await customers.FindByPhoneAsync(phone);
        var address = dialogs.Prompt("Delivery order", "Delivery address", customer?.Address ?? "", multiline: true,
            validate: s => s.Length >= 5 ? null : "Enter the full address");
        if (address is null) return;
        await RunAsync(() => orders.CreateAsync(new CreateOrderRequest(OrderType.Delivery, CustomerUuid: customer?.Uuid,
            DeliveryAddress: address, DeliveryPhone: CustomerService.NormalizePhone(phone))));
    }

    [RelayCommand]
    private async Task OpenOrderAsync(OrderSummary? summary)
    {
        if (summary is null) return;
        Current = await orders.GetAsync(summary.Uuid);
        Show(null);
    }

    [RelayCommand]
    private void CloseOrder() { Current = null; Show(null); }

    // ------------------------------------------------------------------ items

    [RelayCommand]
    private async Task AddTileAsync(MenuTile? tile)
    {
        if (tile is null || _catalog is null) return;
        if (!tile.IsAvailable) { Show($"{tile.Name} is out of stock", true); return; }
        AddItemRequest req;
        if (tile.HasOptions)
        {
            var d = new ItemOptionsDialog(tile.Item, _catalog.VariantsOf(tile.Item.Uuid).ToList(), _catalog.AddonsOf(tile.Item.Uuid).ToList());
            if (dialogs.ShowWindow(d) != true || d.Request is null) return;
            req = d.Request;
        }
        else req = new AddItemRequest(tile.Item.Uuid, null, 1);
        if (!await EnsureOrderAsync()) return;
        var id = CurrentUuid!;
        await RunAsync(() => orders.AddItemAsync(id, req));
    }

    /// <summary>Enter in the search box: exact SKU/barcode (scanner) or the only match is added.</summary>
    [RelayCommand]
    private async Task AddFromSearchAsync()
    {
        var q = SearchText.Trim();
        if (q.Length == 0 || _catalog is null) return;
        var exact = Tiles.FirstOrDefault(t => string.Equals(t.Item.Sku, q, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Item.Barcode, q, StringComparison.OrdinalIgnoreCase));
        var tile = exact ?? (Tiles.Count == 1 ? Tiles[0] : null);
        if (tile is null) { Show(Tiles.Count == 0 ? $"No item matches \"{q}\"" : "Several items match — tap one", Tiles.Count == 0); return; }
        await AddTileAsync(tile);
        SearchText = "";
    }

    [RelayCommand]
    private async Task IncreaseAsync(CartLine? line)
    {
        if (line is null || CurrentUuid is not { } id) return;
        if (!line.IsNew) { await RunAsync(() => orders.AddItemAsync(id, new AddItemRequest(line.Line.MenuItemUuid, null, 1))); return; }
        await RunAsync(() => orders.SetQuantityAsync(id, line.Line.ItemUuid, line.Quantity + 1));
    }

    [RelayCommand]
    private async Task DecreaseAsync(CartLine? line)
    {
        if (line is null || CurrentUuid is not { } id) return;
        if (!line.IsNew) { await RemoveAsync(line); return; }
        await RunAsync(() => orders.SetQuantityAsync(id, line.Line.ItemUuid, line.Quantity - 1));
    }

    [RelayCommand]
    private async Task RemoveAsync(CartLine? line)
    {
        if (line is null || CurrentUuid is not { } id || line.IsCancelled) return;
        string? reason = null;
        if (!line.IsNew)
        {
            reason = dialogs.Prompt("Cancel item", $"The kitchen already has {line.Quantity} × {line.Name}. Reason for cancelling:",
                validate: s => s.Length >= 3 ? null : "Enter a reason (at least 3 characters)");
            if (reason is null) return;
        }
        await RunAsync(() => orders.CancelItemAsync(id, line.Line.ItemUuid, reason));
    }

    [RelayCommand]
    private async Task NoteAsync(CartLine? line)
    {
        if (line is null || CurrentUuid is not { } id) return;
        var note = dialogs.Prompt("Kitchen note", $"Note for {line.Name}", line.Line.Notes ?? "", validate: s => s.Length <= 255 ? null : "Max 255 characters");
        if (note is null) return;
        await RunAsync(() => orders.SetItemNoteAsync(id, line.Line.ItemUuid, note));
    }

    // ------------------------------------------------------------------ order actions

    [RelayCommand] private async Task SendKotAsync() { if (CurrentUuid is { } id) await RunAsync(() => orders.SendToKitchenAsync(id)); }
    [RelayCommand] private async Task HoldAsync() { if (CurrentUuid is { } id) { if (await RunAsync(() => orders.HoldAsync(id))) Current = null; } }
    [RelayCommand] private async Task ResumeAsync() { if (CurrentUuid is { } id) await RunAsync(() => orders.ResumeAsync(id)); }
    [RelayCommand] private async Task BillAsync() { if (CurrentUuid is { } id) await RunAsync(() => orders.BillAsync(id)); }
    [RelayCommand] private async Task ReprintAsync() { if (CurrentUuid is { } id) await RunAsync(() => orders.ReprintAsync(id)); }

    [RelayCommand]
    private async Task PrintVoucherAsync()
    {
        if (Current is not { } v) return;
        if (!v.Lines.Any(l => !l.IsCancelled))
        {
            Show("The order has no items to print.", true);
            return;
        }

        var options = new List<VoucherOption>
        {
            new("bill", "📄 Bill / Guest Voucher", $"Print customer bill voucher for {v.Order.OrderNo}"),
            new("kot", "🍳 Kitchen Order Ticket (KOT)", $"Print kitchen ticket voucher for {v.Order.OrderNo}")
        };
        if (v.Payments.Any() || v.IsClosed)
        {
            options.Add(new("receipt", "🧾 Payment Receipt Voucher", $"Print customer payment receipt voucher for {v.Order.OrderNo}"));
        }

        var pick = dialogs.Choose($"Print Voucher — {v.Order.OrderNo}", options, o => o.Label, o => o.Hint, "Select the type of voucher to print to the POS machine:");
        if (pick is null) return;

        var s = await settings.GetAsync();
        var cashier = session.Current?.FullName ?? "Cashier";
        PrintJob? job = null;

        if (pick.Action == "bill")
        {
            job = ReceiptFormatter.ForCustomer(v, s, ReceiptKind.Bill, cashier, DateTime.UtcNow);
        }
        else if (pick.Action == "receipt")
        {
            job = ReceiptFormatter.ForCustomer(v, s, v.IsClosed ? ReceiptKind.Reprint : ReceiptKind.Receipt, cashier, DateTime.UtcNow);
        }
        else if (pick.Action == "kot")
        {
            var kotItems = v.Lines
                .Where(l => !l.IsCancelled)
                .Select(l => new ReceiptFormatter.KotLine(l.Name, l.Quantity, l.AddonsText, l.Notes))
                .ToList();
            if (kotItems.Count == 0)
            {
                Show("No items found for kitchen ticket.", true);
                return;
            }
            job = ReceiptFormatter.Kot($"VOUCHER-{v.Order.OrderNo}", null, v, kotItems, s, DateTime.UtcNow);
        }

        if (job is not null)
        {
            var err = await printer.PrintAsync(job);
            if (err is not null) Show(err, true);
            else Show($"Voucher printed: {job.Title}");
        }
    }

    [RelayCommand]
    private async Task DiscountAsync()
    {
        if (Current is not { IsClosed: false } v) return;
        var presets = await orders.ListPresetDiscountsAsync();
        var d = new DiscountDialog(presets, v.Order.Subtotal, _settings.MaxDiscountPercentWithoutPermission);
        if (dialogs.ShowWindow(d) != true || d.Request is null) return;
        var req = d.Request;
        await RunAsync(() => orders.ApplyDiscountAsync(v.Order.Uuid, req));
    }

    [RelayCommand]
    private async Task PayAsync()
    {
        while (Current is { IsClosed: false } v)
        {
            if (v.Order.Total == 0 && v.Lines.Any(l => !l.IsCancelled))
            {
                await RunAsync(() => orders.PayAsync(v.Order.Uuid, new PaymentRequest(PaymentMethod.Cash, 0)));
                return;
            }
            var hasCredit = v.Customer is { CreditLimit: > 0 };
            var d = new PaymentDialog(v, _settings.MobileBankingProviders, hasCredit);
            if (dialogs.ShowWindow(d) != true || d.Request is null) return;
            var req = d.Request;
            if (!await RunAsync(() => orders.PayAsync(v.Order.Uuid, req)))
            {
                dialogs.Error(Message ?? "Payment failed");
                continue;
            }
            if (Current?.Order.DueAmount <= 0) return;
        }
    }

    [RelayCommand]
    private async Task CustomerAsync()
    {
        if (Current is not { IsClosed: false } v) return;
        var phone = dialogs.Prompt("Customer", "Mobile number (leave blank to remove the customer)", v.Customer?.Phone ?? "",
            validate: s => s.Length == 0 || CustomerService.IsValidBdMobile(CustomerService.NormalizePhone(s)) ? null : "Enter a valid number (01XXXXXXXXX)");
        if (phone is null) return;
        if (phone.Length == 0) { await RunAsync(() => orders.SetCustomerAsync(v.Order.Uuid, null)); return; }
        var c = await customers.FindByPhoneAsync(phone);
        if (c is null)
        {
            var name = dialogs.Prompt("New customer", $"{CustomerService.NormalizePhone(phone)} is new. Customer name:", validate: s => s.Length >= 2 ? null : "Enter the name");
            if (name is null) return;
            var created = await customers.CreateAsync(new CustomerInput(name, phone, Address: v.Order.DeliveryAddress));
            if (!created.Succeeded) { Show(created.Error, true); return; }
            c = created.Value!;
        }
        var uuid = c.Uuid;
        await RunAsync(() => orders.SetCustomerAsync(v.Order.Uuid, uuid));
    }

    [RelayCommand]
    private async Task DetailsAsync()
    {
        if (Current is not { IsClosed: false } v) return;
        var notes = dialogs.Prompt("Order note", "Note for the whole order (printed on KOT and bill)", v.Order.Notes ?? "", multiline: true,
            validate: s => s.Length <= 500 ? null : "Max 500 characters");
        if (notes is null) return;
        var guests = v.Order.GuestCount;
        if (v.Order.OrderType == OrderType.DineIn)
        {
            var g = dialogs.Prompt("Guests", "Number of guests", guests.ToString(), validate: s => int.TryParse(s, out var n) && n is >= 1 and <= 100 ? null : "Enter 1–100");
            if (g is null) return;
            guests = int.Parse(g);
        }
        await RunAsync(() => orders.UpdateDetailsAsync(v.Order.Uuid, notes, guests, v.Order.DeliveryAddress, v.Order.DeliveryPhone, null));
    }

    [RelayCommand]
    private async Task TransferAsync()
    {
        if (Current is not { IsClosed: false } v || v.Order.OrderType != OrderType.DineIn) { Show("Only dine-in orders can move tables", true); return; }
        var free = (await tables.GetBoardAsync()).Where(t => t.Orders.Count == 0 && t.Table.Status != TableStatus.Cleaning).ToList();
        var pick = dialogs.Choose($"Move {v.Order.OrderNo} to table", free, t => $"{t.Table.Name} ({t.Table.Capacity})", t => t.Table.Section);
        if (pick is null) return;
        await RunAsync(() => orders.TransferTableAsync(v.Order.Uuid, pick.Table.Uuid));
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (Current is not { IsClosed: false } v) return;
        var others = ActiveOrders.Where(o => o.Uuid != v.Order.Uuid).ToList();
        var pick = dialogs.Choose($"Merge into {v.Order.OrderNo}", others, o => o.TableName is null ? o.OrderNo : $"Table {o.TableName}",
            o => $"{o.OrderNo} · {Money.Format(o.Total)}", "All items of the chosen order move to this one, and its table joins this order.");
        if (pick is null) return;
        await RunAsync(() => orders.MergeAsync(v.Order.Uuid, pick.Uuid));
    }

    [RelayCommand]
    private async Task SplitAsync()
    {
        if (Current is not { IsClosed: false } v) return;
        var rows = v.Lines.Where(l => !l.IsCancelled).Select(l => (l, $"{l.Name} — {Money.Format(l.UnitPrice + l.AddonsPerUnit)}", l.Quantity));
        var picked = dialogs.ChooseQuantities("Split bill", "How many of each item go on the NEW bill?", rows);
        if (picked is null) return;
        var selections = picked.Select(p => new SplitSelection(p.Item.ItemUuid, p.Quantity)).ToList();
        await RunAsync(() => orders.SplitAsync(v.Order.Uuid, selections));
    }

    [RelayCommand]
    private async Task VoidAsync()
    {
        if (Current is not { IsClosed: false } v) return;
        var reason = dialogs.Prompt($"Void {v.Order.OrderNo}", "Reason for voiding the whole order", validate: s => s.Length >= 3 ? null : "Enter a reason (at least 3 characters)");
        if (reason is null || !dialogs.Confirm($"Void order {v.Order.OrderNo} ({Money.Format(v.Order.Total)})? This cannot be undone.")) return;
        await RunAsync(() => orders.VoidAsync(v.Order.Uuid, reason));
    }
}
