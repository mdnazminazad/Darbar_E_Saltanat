using System.Windows.Controls;
using DarbarESaltanat.Desktop.Services;

namespace DarbarESaltanat.Desktop.Views;

/// <summary>
/// Light / dark switch. The label names the mode you will switch TO, and follows the theme if it is changed
/// elsewhere (for example from Settings). Style it from XAML like any other button.
/// </summary>
public sealed class ThemeToggleButton : Button
{
    public ThemeToggleButton()
    {
        ToolTip = "Switch between light and dark mode";
        Loaded += (_, _) => { AppTheme.Changed -= OnThemeChanged; AppTheme.Changed += OnThemeChanged; Refresh(); };
        Unloaded += (_, _) => AppTheme.Changed -= OnThemeChanged;
        Refresh();
    }

    protected override void OnClick()
    {
        base.OnClick();
        AppTheme.Toggle();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.Invoke(Refresh);

    private void Refresh() => Content = AppTheme.IsDark ? "☀  Light mode" : "🌙  Dark mode";
}
