using System.Windows;
using System.Windows.Input;
using DarbarESaltanat.Desktop.ViewModels;

namespace DarbarESaltanat.Desktop.Views;

public partial class LoginWindow : Window
{
    public LoginWindow() => InitializeComponent();
    private LoginViewModel Vm => (LoginViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UsernameBox.Focus();
        await Vm.LoadCommand.ExecuteAsync(null);
    }

    // PasswordBox.Password is intentionally not a dependency property; pass it to the VM on change.
    private void OnPasswordChanged(object sender, RoutedEventArgs e) => Vm.Password = PasswordBox.Password;

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm.LoginCommand.CanExecute(null)) Vm.LoginCommand.Execute(null);
    }

    /// <summary>Closing the login screen (X button) exits the app; switching to the main window does not.</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (System.Windows.Application.Current.MainWindow == this) System.Windows.Application.Current.Shutdown();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Physical number keys work in PIN mode.
        if (Vm.PinMode && e.Key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9)
        {
            var digit = e.Key >= Key.NumPad0 ? (e.Key - Key.NumPad0).ToString() : (e.Key - Key.D0).ToString();
            Vm.PinDigitCommand.Execute(digit); e.Handled = true;
        }
        else if (Vm.PinMode && e.Key == Key.Back) { Vm.PinBackspaceCommand.Execute(null); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
