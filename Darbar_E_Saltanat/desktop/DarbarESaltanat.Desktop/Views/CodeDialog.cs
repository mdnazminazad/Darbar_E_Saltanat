using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Domain.Entities;

namespace DarbarESaltanat.Desktop.Views;

/// <summary>Small modal dialogs built in code with the royal theme (prompt, pick-one, pick-quantities, preview).</summary>
public class CodeDialog : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(24, 16, 24, 8) };
    protected readonly StackPanel LeftButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    protected readonly TextBlock ErrorText = new() { Foreground = (Brush)System.Windows.Application.Current.FindResource("DangerTextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

    public CodeDialog(string title, double width = 460)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w && w != this ? w : null;
        Style = (Style)System.Windows.Application.Current.FindResource("RoyalWindow");
        var header = new Border
        {
            Background = (Brush)System.Windows.Application.Current.FindResource("NavyBrush"), Padding = new Thickness(24, 14, 24, 14),
            Child = new TextBlock { Text = title, Foreground = (Brush)System.Windows.Application.Current.FindResource("GoldLightBrush"), FontSize = 18, FontWeight = FontWeights.SemiBold },
        };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new Grid { Margin = new Thickness(24, 8, 24, 20) };
        footer.Children.Add(LeftButtons);
        footer.Children.Add(Buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Body.Children.Add(ErrorText);
        root.Children.Add(new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            try { DialogResult = false; } catch (InvalidOperationException) { Close(); } // modeless windows have no DialogResult
        };
    }

    protected void AddContent(UIElement e) => Body.Children.Insert(Body.Children.Count - 1, e);

    protected static TextBlock Label(string text) => new() { Text = text, Style = (Style)System.Windows.Application.Current.FindResource("FieldLabel"), TextWrapping = TextWrapping.Wrap };

    protected Button AddButton(string text, string style, Action onClick, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { Content = text, Style = (Style)System.Windows.Application.Current.FindResource(style), Margin = new Thickness(8, 0, 0, 0), MinWidth = 110, IsDefault = isDefault, IsCancel = isCancel };
        b.Click += (_, _) => onClick();
        Buttons.Children.Add(b);
        return b;
    }

    protected Button AddLeftButton(string text, string style, Action onClick)
    {
        var b = new Button { Content = text, Style = (Style)System.Windows.Application.Current.FindResource(style), Margin = new Thickness(0, 0, 8, 0), MinWidth = 110 };
        b.Click += (_, _) => onClick();
        LeftButtons.Children.Add(b);
        return b;
    }

    protected void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>Single text input.</summary>
public sealed class PromptDialog : CodeDialog
{
    private readonly TextBox _box;
    private readonly Func<string, string?>? _validate;
    public string Value => _box.Text.Trim();

    public PromptDialog(string title, string label, string initial, bool multiline, Func<string, string?>? validate) : base(title)
    {
        _validate = validate;
        AddContent(Label(label));
        _box = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 90 : 44, VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center };
        AddContent(_box);
        AddButton("Cancel", "BaseButton", () => DialogResult = false, isCancel: true);
        AddButton("OK", "PrimaryButton", Ok, isDefault: !multiline);
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void Ok()
    {
        var err = _validate?.Invoke(Value);
        if (err is not null) { ShowError(err); return; }
        DialogResult = true;
    }
}

/// <summary>Pick one item from a list of large touch buttons.</summary>
public sealed class ChoiceDialog : CodeDialog
{
    public object? Selected { get; private set; }

    public ChoiceDialog(string title, string? message, IEnumerable<(object Item, string Label, string? Hint)> options) : base(title, 520)
    {
        if (message is not null) AddContent(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var panel = new WrapPanel();
        var any = false;
        foreach (var (item, label, hint) in options)
        {
            any = true;
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (hint is not null) content.Children.Add(new TextBlock { Text = hint, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
            var b = new Button { Content = content, Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"), Width = 146, MinHeight = 64, Margin = new Thickness(4) };
            b.Click += (_, _) => { Selected = item; DialogResult = true; };
            panel.Children.Add(b);
        }
        if (!any) panel.Children.Add(new TextBlock { Text = "Nothing to choose from.", Foreground = Brushes.Gray });
        AddContent(panel);
        AddButton("Cancel", "BaseButton", () => DialogResult = false, isCancel: true);
    }
}

/// <summary>Enter a quantity per row (used for split bill).</summary>
public sealed class QuantityDialog : CodeDialog
{
    private readonly List<(object Item, int Max, TextBox Box)> _rows = [];
    public List<(object Item, int Quantity)> Result { get; } = [];

    public QuantityDialog(string title, string message, IEnumerable<(object Item, string Label, int Max)> rows) : base(title, 520)
    {
        AddContent(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var (item, label, max) in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            var box = new TextBox { Text = "0", TextAlignment = TextAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            var minus = new Button { Content = "−", Style = (Style)System.Windows.Application.Current.FindResource("BaseButton"), Padding = new Thickness(0) };
            var plus = new Button { Content = "+", Style = (Style)System.Windows.Application.Current.FindResource("GoldButton"), Padding = new Thickness(0) };
            minus.Click += (_, _) => box.Text = Math.Max(0, Parse(box) - 1).ToString();
            plus.Click += (_, _) => box.Text = Math.Min(max, Parse(box) + 1).ToString();
            var text = new TextBlock { Text = $"{label}  (of {max})", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(minus, 1); Grid.SetColumn(box, 2); Grid.SetColumn(plus, 3);
            g.Children.Add(text); g.Children.Add(minus); g.Children.Add(box); g.Children.Add(plus);
            AddContent(g);
            _rows.Add((item, max, box));
        }
        AddButton("Cancel", "BaseButton", () => DialogResult = false, isCancel: true);
        AddButton("OK", "PrimaryButton", Ok, isDefault: true);
    }

    private static int Parse(TextBox b) => int.TryParse(b.Text, out var n) ? n : 0;

    private void Ok()
    {
        Result.Clear();
        foreach (var (item, max, box) in _rows)
        {
            if (!int.TryParse(box.Text, out var q) || q < 0 || q > max) { ShowError($"Quantities must be whole numbers between 0 and the quantity on the order."); return; }
            if (q > 0) Result.Add((item, q));
        }
        if (Result.Count == 0) { ShowError("Choose at least one item."); return; }
        DialogResult = true;
    }
}

/// <summary>On-screen copy of a receipt/KOT, shown when no printer is configured (or printing failed).</summary>
public sealed class ReceiptPreviewWindow : CodeDialog
{
    private readonly PrintJob _job;
    private readonly TextBlock _statusText;

    private ReceiptPreviewWindow(PrintJob job) : base(job.Title, 560)
    {
        _job = job;
        var note = new TextBlock
        {
            Text = "Document preview. Print directly to your POS machine or select an installed printer.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AddContent(note);
        AddContent(new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            // The slip is always white "paper", so its text must stay black even in dark mode.
            Child = new TextBlock { Text = job.PreviewText, FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = Brushes.Black },
        });

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        AddContent(_statusText);

        // Bottom left corner: Print options for POS Machine and printer selection
        AddLeftButton("🖨️ Print (POS Machine)", "GoldButton", () => _ = PrintToPosAsync());
        AddLeftButton("Select Printer...", "BaseButton", () => _ = SelectPrinterAndPrintAsync());

        // Bottom right corner: Close
        AddButton("Close", "PrimaryButton", () => Close(), isDefault: true, isCancel: true);
    }

    private void ShowStatus(string message, bool isError)
    {
        _statusText.Text = message;
        _statusText.Foreground = (Brush)System.Windows.Application.Current.FindResource(isError ? "DangerTextBrush" : "SuccessTextBrush");
        _statusText.FontWeight = FontWeights.SemiBold;
        _statusText.Visibility = Visibility.Visible;
    }

    private async Task PrintToPosAsync()
    {
        try
        {
            var state = DarbarESaltanat.Desktop.App.Services?.GetService(typeof(ISyncStateStore)) as ISyncStateStore;
            var configured = state != null ? await state.GetAsync(SyncStateKeys.ReceiptPrinter) : null;
            // A saved XPS / PDF / OneNote / Fax printer cannot take raw 80 mm ESC/POS data: use the thermal printer instead.
            var printer = RawPrinter.UsablePrinter(configured);

            if (string.IsNullOrWhiteSpace(printer))
            {
                // No usable thermal printer saved yet: open selector
                await SelectPrinterAndPrintAsync();
                return;
            }

            ShowStatus($"Sending to POS printer '{printer}'...", false);
            await Task.Run(() => RawPrinter.Send(printer, _job.Data, _job.Title));
            ShowStatus($"✓ Printed successfully to POS machine '{printer}'!", false);

            if (state != null && !string.Equals(printer, configured, StringComparison.OrdinalIgnoreCase))
                await state.SetAsync(SyncStateKeys.ReceiptPrinter, printer);
        }
        catch (Exception ex)
        {
            ShowStatus($"POS print failed: {ex.Message} — press 'Select Printer...' and choose your 80 mm thermal printer (check it is switched on and connected).", true);
        }
    }

    private async Task SelectPrinterAndPrintAsync()
    {
        // Only real receipt printers: XPS / PDF / OneNote / Fax cannot print 80 mm thermal slips.
        var installed = RawPrinter.Installed().Where(p => !RawPrinter.IsVirtual(p)).ToList();
        if (installed.Count == 0)
        {
            ShowStatus("No thermal receipt printer found. Switch the printer on, check the USB cable and install the printer driver.", true);
            return;
        }

        var options = new List<(object Item, string Label, string? Hint)>();
        foreach (var p in installed)
        {
            options.Add((p, $"🖨️ {p}", "Thermal / POS printer"));
        }

        var d = new ChoiceDialog("Select POS / Machine Printer", "Choose the printer to send this document to:", options);
        if (d.ShowDialog() != true || d.Selected is null) return;

        if (d.Selected is string selectedPrinter)
        {
            try
            {
                ShowStatus($"Sending to POS printer '{selectedPrinter}'...", false);
                await Task.Run(() => RawPrinter.Send(selectedPrinter, _job.Data, _job.Title));
                ShowStatus($"✓ Printed successfully to POS machine '{selectedPrinter}'!", false);

                var state = DarbarESaltanat.Desktop.App.Services?.GetService(typeof(ISyncStateStore)) as ISyncStateStore;
                if (state != null)
                {
                    await state.SetAsync(SyncStateKeys.ReceiptPrinter, selectedPrinter);
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"Print failed on '{selectedPrinter}': {ex.Message}", true);
            }
        }
    }

    public static void Show(PrintJob job) => new ReceiptPreviewWindow(job).Show();
}
