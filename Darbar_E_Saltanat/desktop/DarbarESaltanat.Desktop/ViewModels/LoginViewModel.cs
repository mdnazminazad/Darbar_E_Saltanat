using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Domain.Entities;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed partial class LoginViewModel(AuthService auth, IUserRepository users, SettingsService settings, WindowCoordinator windows) : ObservableObject
{
    [ObservableProperty] private string _restaurantName = "Darbar_E_Saltanat";
    [ObservableProperty] private bool _pinMode;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(PinLoginCommand))] private bool _busy;
    [ObservableProperty] private User? _selectedPinUser;
    [ObservableProperty] private string _pinMask = "";

    /// <summary>Set from the PasswordBox in code-behind (PasswordBox.Password is not bindable by design).</summary>
    public string Password { private get; set; } = "";
    private string _pin = "";

    public ObservableCollection<User> PinUsers { get; } = [];

    [RelayCommand]
    private async Task LoadAsync()
    {
        RestaurantName = (await settings.GetAsync()).RestaurantName;
        PinUsers.Clear();
        foreach (var u in await users.ListPinUsersAsync()) PinUsers.Add(u);
        SelectedPinUser = PinUsers.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LoginAsync()
    {
        Busy = true; Error = null;
        try
        {
            var r = await auth.LoginAsync(new LoginRequest(Username, Password));
            if (!r.Succeeded) { Error = r.Error; return; }
            windows.ShowMain();
        }
        finally { Busy = false; }
    }

    [RelayCommand] private void ShowPin() { PinMode = true; Error = PinUsers.Count == 0 ? "No users have a PIN yet. A manager can set one in the web portal." : null; }
    [RelayCommand] private void ShowPassword() { PinMode = false; Error = null; }

    [RelayCommand]
    private async Task PinDigitAsync(string digit)
    {
        if (_pin.Length >= 6) return;
        _pin += digit;
        PinMask = new string('●', _pin.Length);
        if (_pin.Length == 6) await PinLoginAsync();
    }

    [RelayCommand] private void PinBackspace() { if (_pin.Length > 0) _pin = _pin[..^1]; PinMask = new string('●', _pin.Length); }
    [RelayCommand] private void PinClear() { _pin = ""; PinMask = ""; }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task PinLoginAsync()
    {
        if (SelectedPinUser is null) { Error = "Choose your name first"; return; }
        Busy = true; Error = null;
        try
        {
            var r = await auth.PinLoginAsync(new PinLoginRequest(SelectedPinUser.Uuid, _pin));
            PinClear();
            if (!r.Succeeded) { Error = r.Error; return; }
            windows.ShowMain();
        }
        finally { Busy = false; }
    }

    [RelayCommand] private void Exit() => windows.Exit();

    private bool NotBusy() => !Busy;
}
