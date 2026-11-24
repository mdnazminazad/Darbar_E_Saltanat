using System.Windows;
using DarbarESaltanat.Desktop.ViewModels;
using DarbarESaltanat.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DarbarESaltanat.Desktop.Services;

/// <summary>Switches between the login screen and the main shell.</summary>
public sealed class WindowCoordinator(IServiceProvider sp)
{
    private Window? _current;

    public void ShowLogin() => Swap(new LoginWindow { DataContext = sp.GetRequiredService<LoginViewModel>() });

    public void ShowMain() => Swap(new MainWindow { DataContext = sp.GetRequiredService<MainViewModel>() });

    public void Exit() => System.Windows.Application.Current.Shutdown();

    private void Swap(Window next)
    {
        var old = _current;
        _current = next;
        System.Windows.Application.Current.MainWindow = next;
        next.Show();
        old?.Close();
    }
}
