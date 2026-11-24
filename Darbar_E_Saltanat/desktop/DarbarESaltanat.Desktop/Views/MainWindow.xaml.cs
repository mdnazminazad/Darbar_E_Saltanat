using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DarbarESaltanat.Desktop.ViewModels;

namespace DarbarESaltanat.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _closingByCoordinator;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IsLocked) && vm.IsLocked) { UnlockBox.Clear(); UnlockBox.Focus(); }
                };
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnUnlockChanged(object sender, RoutedEventArgs e) => Vm.UnlockSecret = UnlockBox.Password;

    private async void OnUnlockClick(object sender, RoutedEventArgs e) { await Vm.UnlockCommand.ExecuteAsync(null); UnlockBox.Clear(); }

    private async void OnUnlockKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await Vm.UnlockCommand.ExecuteAsync(null);
        UnlockBox.Clear();
    }

    /// <summary>Closing the main window exits the app (after confirmation); switching users replaces it silently.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingByCoordinator || System.Windows.Application.Current.MainWindow != this) return;
        var r = MessageBox.Show("Exit Darbar_E_Saltanat? Unsynced changes stay safely on this computer.", "Exit",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        _closingByCoordinator = true;
        System.Windows.Application.Current.Shutdown();
    }
}
