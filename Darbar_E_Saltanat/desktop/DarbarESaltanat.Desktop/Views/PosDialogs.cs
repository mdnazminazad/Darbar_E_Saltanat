using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using MenuItem = DarbarESaltanat.Domain.Entities.MenuItem;

namespace DarbarESaltanat.Desktop.Views;

internal static class Ui
{
    public static Style S(string key) => (Style)System.Windows.Application.Current.FindResource(key);
    public static bool TryMoney(string text, out decimal value) =>
        decimal.TryParse(text.Replace(",", "").Replace("৳", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value) && value >= 0;
}

/// <summary>Size, add-ons, quantity and kitchen note for one menu item.</summary>
public sealed class ItemOptionsDialog : CodeDialog
{
    private readonly MenuItem _item;
    private readonly List<(ItemVariant Variant, ToggleButton Button)> _variants = [];
    private readonly List<(ItemAddon Addon, int Max, TextBlock Qty)> _addons = [];
    private readonly TextBlock _qtyText = new() { Text = "1", FontSize = 22, MinWidth = 48, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _notes = new();
    private readonly TextBlock _price = new() { FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 0) };
    private int _qty = 1;

    public AddItemRequest? Request { get; private set; }

    public ItemOptionsDialog(MenuItem item, IReadOnlyList<ItemVariant> variants, IReadOnlyList<(ItemAddon Addon, int MaxQty)> addons) : base(item.Name, 560)
    {
        _item = item;
        if (variants.Count > 0)
        {
            AddContent(Label("Size / variant"));
            var wrap = new WrapPanel();
            var selected = variants.FirstOrDefault(v => v.IsDefault) ?? variants[0];
            foreach (var v in variants)
            {
                var b = new ToggleButton { Content = $"{v.Name}\n{Money.Format(v.Price)}", MinWidth = 120, MinHeight = 56, Margin = new Thickness(0, 0, 8, 8), IsChecked = v == selected, FontSize = 15 };
                b.Click += (_, _) => { foreach (var (_, other) in _variants) other.IsChecked = other == b; Update(); };
                _variants.Add((v, b));
                wrap.Children.Add(b);
            }
            AddContent(wrap);
        }
        if (addons.Count > 0)
        {
            AddContent(Label("Add-ons"));
            foreach (var (addon, max) in addons)
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                foreach (var w in new[] { 52, 44, 52 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
                var qty = new TextBlock { Text = "0", FontSize = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var minus = new Button { Content = "−", Style = Ui.S("BaseButton"), Padding = new Thickness(0) };
                var plus = new Button { Content = "+", Style = Ui.S("GoldButton"), Padding = new Thickness(0) };
                minus.Click += (_, _) => { qty.Text = Math.Max(0, int.Parse(qty.Text) - 1).ToString(); Update(); };
                plus.Click += (_, _) => { qty.Text = Math.Min(max, int.Parse(qty.Text) + 1).ToString(); Update(); };
                var label = new TextBlock { Text = $"{addon.Name}  (+{Money.Format(addon.Price)}, max {max})", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(minus, 1); Grid.SetColumn(qty, 2); Grid.SetColumn(plus, 3);
                g.Children.Add(label); g.Children.Add(minus); g.Children.Add(qty); g.Children.Add(plus);
                AddContent(g);
                _addons.Add((addon, max, qty));
            }
        }
        AddContent(Label("Quantity"));
        var qrow = new StackPanel { Orientation = Orientation.Horizontal };
        var qm = new Button { Content = "−", Style = Ui.S("KeypadButton"), Width = 64 };
        var qp = new Button { Content = "+", Style = Ui.S("KeypadButton"), Width = 64 };
        qm.Click += (_, _) => { _qty = Math.Max(1, _qty - 1); Update(); };
        qp.Click += (_, _) => { _qty = Math.Min(OrderService.MaxLineQuantity, _qty + 1); Update(); };
        qrow.Children.Add(qm); qrow.Children.Add(_qtyText); qrow.Children.Add(qp);
        AddContent(qrow);
        AddContent(Label("Kitchen note (optional, e.g. less spicy, no onion)"));
        AddContent(_notes);
        AddContent(_price);
        AddButton("Cancel", "BaseButton", () => DialogResult = false, isCancel: true);
        AddButton("Add to order", "PrimaryButton", Ok, isDefault: true);
        Update();
    }

    private ItemVariant? SelectedVariant => _variants.FirstOrDefault(v => v.Button.IsChecked == true).Variant;

    private void Update()
    {
        _qtyText.Text = _qty.ToString(CultureInfo.InvariantCulture);
        var unit = (SelectedVariant?.Price ?? _item.SellingPrice) + _addons.Sum(a => a.Addon.Price * int.Parse(a.Qty.Text));
        _price.Text = $"{_qty} × {Money.Format(unit)} = {Money.Format(unit * _qty)}";
    }

    private void Ok()
    {
        if (_variants.Count > 0 && SelectedVariant is null) { ShowError("Choose a size"); return; }
        if (_notes.Text.Length > 255) { ShowError("Note is too long (max 255 characters)"); return; }
        var adds = _addons.Select(a => new AddonSelection(a.Addon.Uuid, int.Parse(a.Qty.Text))).Where(a => a.Quantity > 0).ToList();
        Request = new AddItemRequest(_item.Uuid, SelectedVariant?.Uuid, _qty, adds, string.IsNullOrWhiteSpace(_notes.Text) ? null : _notes.Text.Trim());
        DialogResult = true;
    }
}

/// <summary>Collects one payment. Supports split payments: the POS reopens it while an amount is still due.</summary>
public sealed class PaymentDialog : CodeDialog
{
    private readonly decimal _due;
    private readonly TextBox _amount = new() { FontSize = 20 };
    private readonly TextBox _tendered = new() { FontSize = 20 };
    private readonly TextBox _reference = new();
    private readonly ComboBox _provider = new() { MinHeight = 44, FontSize = 15 };
    private readonly TextBlock _change = new() { FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _cashPanel = new();
    private readonly StackPanel _refPanel = new();
    private readonly List<(string Method, ToggleButton Button)> _methods = [];
    private string _method = PaymentMethod.Cash;
    private readonly IReadOnlyList<string> _mobileProviders;
    private static readonly string[] CardNetworks = ["Visa", "Mastercard", "Amex", "UnionPay", "Other"];

    public PaymentRequest? Request { get; private set; }

    public PaymentDialog(OrderView v, IReadOnlyList<string> providers, bool customerHasCredit) : base($"Payment — {v.Order.OrderNo}", 560)
    {
        _due = v.Order.DueAmount;
        _mobileProviders = providers;
        var summary = new TextBlock { FontSize = 16, TextWrapping = TextWrapping.Wrap };
        summary.Inlines.Add(new System.Windows.Documents.Run($"Total {Money.Format(v.Order.Total)}   ·   Paid {Money.Format(v.TotalPayments)}\n"));
        summary.Inlines.Add(new System.Windows.Documents.Run($"DUE {Money.Format(_due)}") { FontSize = 26, FontWeight = FontWeights.Bold, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("DangerTextBrush") });
        AddContent(summary);

        AddContent(Label("Method"));
        var methods = new UniformGrid { Columns = 4 };
        foreach (var m in new[] { PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.MobileBanking, PaymentMethod.Credit })
        {
            if (m == PaymentMethod.Credit && !customerHasCredit) continue;
            var b = new ToggleButton { Content = PaymentMethod.Label(m), MinHeight = 56, Margin = new Thickness(0, 0, 6, 0), FontSize = 15, IsChecked = m == _method };
            b.Click += (_, _) => SelectMethod(m);
            _methods.Add((m, b));
            methods.Children.Add(b);
        }
        AddContent(methods);

        AddContent(Label("Amount to apply"));
        _amount.Text = _due.ToString("0.00", CultureInfo.InvariantCulture);
        _amount.TextChanged += (_, _) => UpdateChange();
        AddContent(_amount);

        _cashPanel.Children.Add(Label("Cash received"));
        _tendered.TextChanged += (_, _) => UpdateChange();
        _cashPanel.Children.Add(_tendered);
        var quick = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var suggestions = new List<decimal> { _due };
        foreach (var note in new[] { 100m, 500m, 1000m })
        {
            var rounded = Math.Ceiling(_due / note) * note;
            if (rounded > _due && !suggestions.Contains(rounded)) suggestions.Add(rounded);
        }
        foreach (var amount in suggestions.Take(4))
        {
            var b = new Button { Content = amount == _due ? "Exact" : Money.Format(amount), Style = Ui.S("GoldButton"), Margin = new Thickness(0, 0, 6, 6), MinWidth = 96 };
            b.Click += (_, _) => _tendered.Text = amount.ToString("0.00", CultureInfo.InvariantCulture);
            quick.Children.Add(b);
        }
        _cashPanel.Children.Add(quick);
        _cashPanel.Children.Add(_change);
        AddContent(_cashPanel);

        _refPanel.Children.Add(Label("Provider"));
        _refPanel.Children.Add(_provider);
        _refPanel.Children.Add(Label("Transaction ID / reference"));
        _refPanel.Children.Add(_reference);
        AddContent(_refPanel);

        AddButton("Cancel", "BaseButton", () => DialogResult = false, isCancel: true);
        AddButton("Take payment (Enter)", "PrimaryButton", Ok, isDefault: true);
        SelectMethod(PaymentMethod.Cash);
        Loaded += (_, _) => { _tendered.Focus(); };
    }

    private void SelectMethod(string m)
    {
        _method = m;
        foreach (var (method, b) in _methods) b.IsChecked = method == m;
        _cashPanel.Visibility = m == PaymentMethod.Cash ? Visibility.Visible : Visibility.Collapsed;
        _refPanel.Visibility = m is PaymentMethod.MobileBanking or PaymentMethod.Card ? Visibility.Visible : Visibility.Collapsed;
        _provider.ItemsSource = m == PaymentMethod.Card ? CardNetworks : _mobileProviders;
        _provider.SelectedIndex = -1;
        UpdateChange();
    }

    private void UpdateChange()
    {
        if (_method != PaymentMethod.Cash) { _change.Text = ""; return; }
        if (Ui.TryMoney(_amount.Text, out var amount) && Ui.TryMoney(_tendered.Text, out var tendered) && tendered >= amount)
            _change.Text = $"Change: {Money.Format(tendered - amount)}";
        else _change.Text = "";
    }

    private void Ok()
    {
        if (!Ui.TryMoney(_amount.Text, out var amount) || amount <= 0) { ShowError("Enter the amount to apply"); return; }
        decimal? tendered = null;
        if (_method == PaymentMethod.Cash)
        {
            if (string.IsNullOrWhiteSpace(_tendered.Text)) tendered = amount;
            else if (!Ui.TryMoney(_tendered.Text, out var t)) { ShowError("Enter the cash received"); return; }
            else tendered = t;
        }
        Request = new PaymentRequest(_method, amount, tendered, _provider.SelectedItem as string,
            string.IsNullOrWhiteSpace(_reference.Text) ? null : _reference.Text.Trim());
        DialogResult = true;
    }
}

/// <summary>Preset discount, manual % / fixed, coupon code, or remove.</summary>
public sealed class DiscountDialog : CodeDialog
{
    private readonly TextBox _value = new() { FontSize = 18 };
    private readonly TextBox _coupon = new() { CharacterCasing = CharacterCasing.Upper, FontSize = 18 };
    private readonly RadioButton _percent = new() { Content = "Percent (%)", IsChecked = true, Margin = new Thickness(0, 0, 16, 0), FontSize = 15 };
    private readonly RadioButton _fixed = new() { Content = "Fixed amount (৳)", FontSize = 15 };

    public DiscountRequest? Request { get; private set; }

    public DiscountDialog(IReadOnlyList<Discount> presets, decimal subtotal, decimal maxPercentWithoutOverride) : base("Discount", 560)
    {
        AddContent(new TextBlock { Text = $"Subtotal {Money.Format(subtotal)}. Discounts above {maxPercentWithoutOverride:0.##}% need a manager override.", TextWrapping = TextWrapping.Wrap });
        if (presets.Count > 0)
        {
            AddContent(Label("Quick discounts"));
            var wrap = new WrapPanel();
            foreach (var p in presets)
            {
                var b = new Button { Content = p.Name + (p.RequiresPermission ? " 🔒" : ""), Style = Ui.S("GoldButton"), Margin = new Thickness(0, 0, 8, 8) };
                b.Click += (_, _) => { Request = new DiscountRequest(p.Type, p.Value, PresetUuid: p.Uuid); DialogResult = true; };
                wrap.Children.Add(b);
            }
            AddContent(wrap);
        }
        AddContent(Label("Manual discount"));
        var types = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        types.Children.Add(_percent); types.Children.Add(_fixed);
        AddContent(types);
        AddContent(_value);
        var apply = new Button { Content = "Apply manual discount", Style = Ui.S("PrimaryButton"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        apply.Click += (_, _) =>
        {
            if (!Ui.TryMoney(_value.Text, out var v) || v <= 0) { ShowError("Enter a discount value greater than zero"); return; }
            if (_percent.IsChecked == true && v > 100) { ShowError("Percent cannot exceed 100"); return; }
            Request = new DiscountRequest(_percent.IsChecked == true ? DiscountType.Percent : DiscountType.Fixed, v);
            DialogResult = true;
        };
        AddContent(apply);
        AddContent(Label("Coupon code"));
        AddContent(_coupon);
        var useCoupon = new Button { Content = "Apply coupon", Style = Ui.S("PrimaryButton"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        useCoupon.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_coupon.Text)) { ShowError("Enter the coupon code"); return; }
            Request = new DiscountRequest(DiscountType.Coupon, CouponCode: _coupon.Text.Trim());
            DialogResult = true;
        };
        AddContent(useCoupon);
        AddButton("Remove discount", "DangerButton", () => { Request = DiscountRequest.Clear(); DialogResult = true; });
        AddButton("Cancel", "BaseButton", () => DialogResult = false, isCancel: true);
    }
}
