using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Application.Sync;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Domain.Security;
using DarbarESaltanat.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed record NavItem(string Key, string Title, string Shortcut, Func<ObservableObject> Create);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ICurrentSession _session;
    private readonly AuthService _auth;
    private readonly WindowCoordinator _windows;
    private readonly InactivityMonitor _inactivity;
    private readonly SettingsService _settings;
    private readonly IDeviceContext _device;
    private readonly SyncWorker _syncWorker;
    private readonly ISyncStore _syncStore;
    private readonly IClock _clock;
    private readonly ILogger<MainViewModel> _log;
    private bool _navigating;

    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string? _unlockError;
    [ObservableProperty] private string _restaurantName = "Darbar_E_Saltanat";

    public SyncStatusMonitor Sync { get; }
    public string UserName => _session.Current?.FullName ?? "";
    public string Roles => string.Join(", ", _session.Current?.Roles ?? Array.Empty<string>());
    public string DeviceId => _device.DeviceId.Length > 8 ? _device.DeviceId[..8] : _device.DeviceId;
    public string BranchId => _device.BranchUuid.Length > 8 ? _device.BranchUuid[..8] : _device.BranchUuid;
    public ObservableCollection<NavItem> Navigation { get; } = [];
    public string UnlockSecret { private get; set; } = "";
    public string ShortcutHelp => string.Join(" · ", Navigation.Select(n => $"{n.Shortcut} {n.Title}")) + " · F12 Lock";

    public MainViewModel(IServiceProvider sp, ICurrentSession session, AuthService auth, WindowCoordinator windows,
                         InactivityMonitor inactivity, SyncStatusMonitor sync, SettingsService settings,
                         IDeviceContext device, SyncWorker syncWorker, ISyncStore syncStore, IClock clock,
                         Navigator navigator, ILogger<MainViewModel> log)
    {
        _session = session; _auth = auth; _windows = windows; _inactivity = inactivity; _settings = settings;
        _device = device; _syncWorker = syncWorker; _syncStore = syncStore; _clock = clock;
        _log = log; Sync = sync;

        void Add<T>(string key, string title, string shortcut, string? permission = null) where T : ObservableObject
        {
            if (permission is null || session.Has(permission)) Navigation.Add(new NavItem(key, title, shortcut, () => sp.GetRequiredService<T>()));
        }
        Add<DashboardViewModel>(PageKeys.Dashboard, "Dashboard", "F1", Permissions.OrdersView);
        Add<PosViewModel>(PageKeys.Pos, "POS", "F2", Permissions.OrdersCreate);
        Add<TablesViewModel>(PageKeys.Tables, "Tables", "F3", Permissions.OrdersView);
        Add<OrdersViewModel>(PageKeys.Orders, "Orders", "F4", Permissions.OrdersView);
        Add<MenuManagementViewModel>(PageKeys.Menu, "Menu", "F5", Permissions.MenuManage);
        Add<UsersViewModel>(PageKeys.Users, "Users", "F7", Permissions.UsersManage);
        Add<RolesViewModel>(PageKeys.Roles, "Roles", "F8", Permissions.RolesManage);
        Add<SyncViewModel>(PageKeys.Sync, "Sync Center", "Shift+F9", Permissions.SyncView);
        if (session.Has(Permissions.SettingsManage) || session.Has(Permissions.SyncManage))
            Navigation.Add(new NavItem(PageKeys.Settings, "Settings", "F9", () => sp.GetRequiredService<SettingsViewModel>()));

        navigator.Requested += (key, arg) => _ = OpenAsync(key, arg);
        _session.Changed += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() => IsLocked = _session.IsLocked);
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        var s = await _settings.GetAsync();
        RestaurantName = s.RestaurantName;
        _inactivity.Start(TimeSpan.FromMinutes(s.AutoLockMinutes));
        _settings.SettingsChanged += (_, x) => { RestaurantName = x.RestaurantName; _inactivity.Timeout = TimeSpan.FromMinutes(x.AutoLockMinutes); };
        Sync.Start();
        var start = Navigation.FirstOrDefault(n => n.Key == PageKeys.Pos) ?? Navigation.FirstOrDefault();
        if (start is not null) await OpenAsync(start.Key, null);
    }

    /// <summary>Creates the page, disposes the previous one, and loads it (optionally with an argument such as an order uuid).</summary>
    private async Task OpenAsync(string key, object? argument)
    {
        var item = Navigation.FirstOrDefault(n => n.Key == key);
        if (item is null) return;
        _navigating = true;
        SelectedNav = item;
        _navigating = false;
        (CurrentPage as IDisposable)?.Dispose();
        var page = item.Create();
        CurrentPage = page;
        try
        {
            if (page is IPage p) await p.LoadAsync(argument);
        }
        catch (DarbarESaltanat.Application.Common.PermissionDeniedException ex) { System.Windows.MessageBox.Show(ex.Message, "Permission required"); }
        catch (Exception ex) { _log.LogError(ex, "Loading page {Page} failed", key); System.Windows.MessageBox.Show($"Could not load {item.Title}: {ex.Message}", "Darbar_E_Saltanat"); }
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (!_navigating && value is not null) _ = OpenAsync(value.Key, null);
    }

    [RelayCommand] private void Navigate(NavItem item) => SelectedNav = item;
    [RelayCommand] private void NavigateShortcut(string key) { var n = Navigation.FirstOrDefault(x => x.Shortcut == key); if (n is not null) _ = OpenAsync(n.Key, null); }

    [RelayCommand] private void Lock() => _session.Lock();

    [RelayCommand]
    private void SyncNow()
    {
        _syncWorker.TriggerNow();
        _ = Sync.RefreshAsync();
    }

    [RelayCommand]
    private async Task RetryFailedSyncAsync()
    {
        await _syncStore.RetryFailedAsync(_clock.UtcNow);
        _syncWorker.TriggerNow();
        await Sync.RefreshAsync();
    }

    [RelayCommand]
    private async Task OpenSyncCenterAsync() => await OpenAsync(PageKeys.Sync, null);

    [RelayCommand]
    private async Task UnlockAsync()
    {
        UnlockError = null;
        var r = await _auth.UnlockAsync(UnlockSecret);
        if (!r.Succeeded) UnlockError = r.Error;
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        _inactivity.Stop();
        (CurrentPage as IDisposable)?.Dispose();
        await _auth.LogoutAsync();
        _windows.ShowLogin();
    }
}
