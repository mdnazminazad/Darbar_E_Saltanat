using System.Windows;
using DarbarESaltanat.Desktop.Views;

namespace DarbarESaltanat.Desktop.Services;

/// <summary>Modal dialogs for view-models (keeps Window code out of the VMs).</summary>
public sealed class DialogService
{
    public string? Prompt(string title, string label, string initial = "", bool multiline = false, Func<string, string?>? validate = null)
    {
        var d = new PromptDialog(title, label, initial, multiline, validate);
        return d.ShowDialog() == true ? d.Value : null;
    }

    public T? Choose<T>(string title, IEnumerable<T> items, Func<T, string> label, Func<T, string?>? hint = null, string? message = null) where T : class
    {
        var d = new ChoiceDialog(title, message, items.Select(i => ((object)i, label(i), hint?.Invoke(i))));
        return d.ShowDialog() == true ? d.Selected as T : null;
    }

    public List<(T Item, int Quantity)>? ChooseQuantities<T>(string title, string message, IEnumerable<(T Item, string Label, int Max)> rows) where T : class
    {
        var d = new QuantityDialog(title, message, rows.Select(r => ((object)r.Item, r.Label, r.Max)));
        return d.ShowDialog() == true ? d.Result.Select(r => ((T)r.Item, r.Quantity)).ToList() : null;
    }

    public bool Confirm(string message, string title = "Please confirm") =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Error(string message, string title = "Darbar_E_Saltanat") =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Info(string message, string title = "Darbar_E_Saltanat") =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool? ShowWindow(Window w)
    {
        w.Owner = Owner();
        return w.ShowDialog();
    }

    private static Window Owner() => System.Windows.Application.Current.MainWindow;
}
