using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Security;
using DarbarESaltanat.Infrastructure.Persistence;
using DarbarESaltanat.Application.Sync;
using DarbarESaltanat.Infrastructure.Sync;
using Microsoft.Extensions.Logging;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IPage
{
    private readonly SettingsService _settings;
    private readonly ISyncStateStore _state;
    private readonly ISecretProtector? _protector;
    private readonly DeviceContext _device;
    private readonly SyncStatusMonitor _sync;
    private readonly ILogger<SettingsViewModel> _log;
    private readonly PrintService _printer;
    private readonly SyncEngine _engine;
    private readonly SyncWorker _worker;

    public bool CanEditRestaurant { get; }
    public bool CanEditSync { get; }

    // Restaurant
    [ObservableProperty] private string _restaurantName = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _vatRate = "";
    [ObservableProperty] private bool _vatInclusive;
    [ObservableProperty] private string _serviceChargeRate = "";
    [ObservableProperty] private bool _serviceChargeDineInOnly;
    [ObservableProperty] private string _maxDiscount = "";
    [ObservableProperty] private string _receiptHeader = "";
    [ObservableProperty] private string _receiptFooter = "";
    [ObservableProperty] private int _receiptWidthMm = 80;
    [ObservableProperty] private string _autoLockMinutes = "";
    [ObservableProperty] private string _syncIntervalSeconds = "";
    [ObservableProperty] private string _theme = "dark";

    // Sync
    [ObservableProperty] private string _apiBaseUrl = "";
    [ObservableProperty] private string _deviceId = "";
    [ObservableProperty] private string _newDeviceToken = "";
    [ObservableProperty] private bool _hasDeviceToken;

    // Printers (stored on this computer only)
    public IReadOnlyList<string> InstalledPrinters { get; } = ["(none — show on screen)", .. RawPrinter.Installed()];
    [ObservableProperty] private string? _receiptPrinter;
    [ObservableProperty] private string? _kitchenPrinter;
    [ObservableProperty] private bool _cashDrawerEnabled;

    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    public int[] ReceiptWidths { get; } = [58, 80];
    public string[] Themes { get; } = ["dark", "light"];

    public SettingsViewModel(SettingsService settings, ISyncStateStore state, DeviceContext device, ICurrentSession session,
                             SyncStatusMonitor sync, ILogger<SettingsViewModel> log, IServiceProvider sp, PrintService printer, SyncEngine engine, SyncWorker worker)
    {
        _engine = engine; _worker = worker;
        _settings = settings; _state = state; _device = device; _sync = sync; _log = log; _printer = printer;
        _protector = sp.GetService(typeof(ISecretProtector)) as ISecretProtector;
        CanEditRestaurant = session.Has(Permissions.SettingsManage);
        CanEditSync = session.Has(Permissions.SyncManage);
    }

    public async Task LoadAsync(object? argument)
    {
        var s = await _settings.GetAsync(refresh: true);
        RestaurantName = s.RestaurantName; Address = s.RestaurantAddress ?? ""; Phone = s.RestaurantPhone ?? "";
        VatRate = s.VatRate.ToString(CultureInfo.InvariantCulture); VatInclusive = s.VatInclusive;
        ServiceChargeRate = s.ServiceChargeRate.ToString(CultureInfo.InvariantCulture); ServiceChargeDineInOnly = s.ServiceChargeDineInOnly;
        MaxDiscount = s.MaxDiscountPercentWithoutPermission.ToString(CultureInfo.InvariantCulture);
        ReceiptHeader = s.ReceiptHeader; ReceiptFooter = s.ReceiptFooter; ReceiptWidthMm = s.ReceiptWidthMm;
        AutoLockMinutes = s.AutoLockMinutes.ToString(CultureInfo.InvariantCulture); SyncIntervalSeconds = s.SyncIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        Theme = s.Theme;
        ApiBaseUrl = await _state.GetAsync(SyncStateKeys.ApiBaseUrl) ?? "";
        DeviceId = _device.DeviceId;
        HasDeviceToken = !string.IsNullOrEmpty(await _state.GetAsync(SyncStateKeys.DeviceTokenProtected));
        ReceiptPrinter = await _state.GetAsync(SyncStateKeys.ReceiptPrinter) is { Length: > 0 } rp ? rp : InstalledPrinters[0];
        KitchenPrinter = await _state.GetAsync(SyncStateKeys.KitchenPrinter) is { Length: > 0 } kp ? kp : InstalledPrinters[0];
        CashDrawerEnabled = await _state.GetAsync(SyncStateKeys.CashDrawerEnabled) == "true";
    }

    private string? PrinterValue(string? selection) => selection is null || selection == InstalledPrinters[0] ? null : selection;

    [RelayCommand]
    private async Task SavePrintersAsync()
    {
        if (!CanEditRestaurant) { Show("You do not have permission to change printers.", true); return; }
        await _state.SetAsync(SyncStateKeys.ReceiptPrinter, PrinterValue(ReceiptPrinter));
        await _state.SetAsync(SyncStateKeys.KitchenPrinter, PrinterValue(KitchenPrinter));
        await _state.SetAsync(SyncStateKeys.CashDrawerEnabled, CashDrawerEnabled ? "true" : "false");
        Show("Printer settings saved for this computer.", false);
    }

    [RelayCommand]
    private async Task TestPrinterAsync(string? which)
    {
        var name = PrinterValue(which == "kitchen" ? KitchenPrinter : ReceiptPrinter);
        if (name is null) { Show("Choose a printer first.", true); return; }
        var err = await _printer.TestAsync(name);
        Show(err is null ? $"Test page sent to {name}." : $"Test failed: {err}", err is not null);
    }

    private void Show(string msg, bool error) { Message = msg; IsError = error; }

    [RelayCommand]
    private async Task SaveRestaurantAsync()
    {
        static bool Dec(string s, out decimal d) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d);
        static bool Int(string s, out int i) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
        if (!Dec(VatRate, out var vat) || !Dec(ServiceChargeRate, out var sc) || !Dec(MaxDiscount, out var md)
            || !Int(AutoLockMinutes, out var lockMin) || !Int(SyncIntervalSeconds, out var syncSec))
        { Show("Please enter valid numbers (use . for decimals).", true); return; }

        var r = await _settings.SaveAsync(new RestaurantSettings
        {
            RestaurantName = RestaurantName, RestaurantAddress = Address, RestaurantPhone = Phone,
            VatRate = vat, VatInclusive = VatInclusive, ServiceChargeRate = sc, ServiceChargeDineInOnly = ServiceChargeDineInOnly,
            MaxDiscountPercentWithoutPermission = md, ReceiptHeader = ReceiptHeader, ReceiptFooter = ReceiptFooter,
            ReceiptWidthMm = ReceiptWidthMm, AutoLockMinutes = lockMin, SyncIntervalSeconds = syncSec, Theme = Theme,
        });
        Show(r.Succeeded ? "Settings saved on this computer. They will sync to the server automatically." : r.Error!, !r.Succeeded);
        await _sync.RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveSyncAsync()
    {
        if (!CanEditSync) { Show("You do not have permission to change sync settings.", true); return; }
        var url = ApiBaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback))
        { Show("API base URL must be an https:// address (http is allowed only for localhost testing).", true); return; }
        if (!Guid.TryParse(DeviceId.Trim(), out _)) { Show("Device ID must be the UUID shown when the device was registered in the web portal.", true); return; }
        if (_protector is null) { Show("Secure storage is unavailable on this system.", true); return; }

        try
        {
            await _state.SetAsync(SyncStateKeys.ApiBaseUrl, url);
            await _state.SetAsync(SyncStateKeys.DeviceId, DeviceId.Trim());
            if (!string.IsNullOrWhiteSpace(NewDeviceToken))
                await _state.SetAsync(SyncStateKeys.DeviceTokenProtected, _protector.Protect(NewDeviceToken.Trim()));
            _device.Set(DeviceId.Trim(), _device.BranchUuid);
            NewDeviceToken = "";
            HasDeviceToken = !string.IsNullOrEmpty(await _state.GetAsync(SyncStateKeys.DeviceTokenProtected));

            // Verify the connection and adopt the branch this device was registered to.
            var token = _protector.Unprotect(await _state.GetAsync(SyncStateKeys.DeviceTokenProtected));
            try
            {
                var info = await _engine.TestConnectionAsync(new SyncEndpoint(url, DeviceId.Trim(), token ?? ""));
                if (!string.IsNullOrEmpty(info.BranchUuid) && info.BranchUuid != _device.BranchUuid)
                {
                    await _state.SetAsync(SyncStateKeys.BranchUuid, info.BranchUuid);
                    _device.Set(DeviceId.Trim(), info.BranchUuid);
                }
                Show($"Connected as “{info.Name}” ({info.BranchName}). The token is encrypted for this Windows user. Syncing now…", false);
                _worker.TriggerNow();
            }
            catch (SyncAuthException ex) { Show($"Saved, but the server refused this device: {ex.Message}. Check the Device ID and token.", true); }
            catch (SyncOfflineException ex) { Show($"Saved. The server could not be reached right now ({ex.Message}); sync will start when it is online.", true); }
            catch (SyncRejectedException ex) { Show($"Saved, but the address looks wrong: {ex.Message}", true); }
            await _sync.RefreshAsync();
        }
        catch (Exception ex) { _log.LogError(ex, "Saving sync settings failed"); Show("Could not save sync settings. See the log for details.", true); }
    }
}
