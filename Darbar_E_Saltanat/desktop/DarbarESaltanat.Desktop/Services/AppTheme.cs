using System.IO;
using System.Windows;

namespace DarbarESaltanat.Desktop.Services;

/// <summary>
/// Light / dark theme for THIS computer. The choice is kept in %LOCALAPPDATA%\DarbarESaltanat\theme.txt so it is
/// available before the database opens (the login screen already follows it) and is not synced to other computers.
/// Light is the default; dark = Themes/Palette.Dark.xaml merged on top of Themes/Royal.xaml.
/// </summary>
public static class AppTheme
{
    public const string Light = "light";
    public const string Dark = "dark";

    private static ResourceDictionary? _dark;

    public static string Current { get; private set; } = Light;
    public static bool IsDark => Current == Dark;

    /// <summary>Raised after the theme changed (any thread that called <see cref="Apply"/>, normally the UI thread).</summary>
    public static event EventHandler? Changed;

    private static string PrefFile => Path.Combine(App.DataDir, "theme.txt");

    /// <summary>Call once at start-up, before the first window opens.</summary>
    public static void Initialize()
    {
        var saved = Light;
        try { if (File.Exists(PrefFile)) saved = Normalize(File.ReadAllText(PrefFile)); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Could not read the saved theme; using light"); }
        Apply(saved, persist: false);
    }

    public static void Toggle() => Apply(IsDark ? Light : Dark);

    public static void Apply(string? theme, bool persist = true)
    {
        var target = Normalize(theme);
        var app = System.Windows.Application.Current;
        if (app is null) return;
        if (target == Current) return;   // nothing to switch (also keeps background-thread callers harmless)
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Apply(target, persist));
            return;
        }
        try
        {
            var merged = app.Resources.MergedDictionaries;
            if (target == Dark)
            {
                _dark ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Palette.Dark.xaml", UriKind.Absolute) };
                if (!merged.Contains(_dark)) merged.Add(_dark);
            }
            else if (_dark is not null)
            {
                merged.Remove(_dark);
            }
        }
        catch (Exception ex)
        {
            // Never take the app down over a colour scheme: stay on whatever is showing now.
            Serilog.Log.Error(ex, "Could not apply the {Theme} theme", target);
            return;
        }

        Current = target;
        if (persist)
        {
            try { Directory.CreateDirectory(App.DataDir); File.WriteAllText(PrefFile, target); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Could not save the theme choice"); }
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string Normalize(string? t) => string.Equals(t?.Trim(), Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;
}
