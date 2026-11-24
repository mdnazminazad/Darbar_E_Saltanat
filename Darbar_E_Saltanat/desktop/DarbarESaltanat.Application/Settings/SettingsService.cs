using System.Globalization;
using System.Text.Json;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Security;
using FluentValidation;

namespace DarbarESaltanat.Application.Settings;

/// <summary>Typed view of the settings the desktop uses (receipt, tax, security, sync).</summary>
public sealed record RestaurantSettings
{
    public string RestaurantName { get; init; } = "Darbar_E_Saltanat";
    public string? RestaurantAddress { get; init; }
    public string? RestaurantPhone { get; init; }
    public string CurrencySymbol { get; init; } = "৳";
    public decimal VatRate { get; init; } = 5m;
    public bool VatInclusive { get; init; }
    public decimal ServiceChargeRate { get; init; } = 10m;
    public bool ServiceChargeDineInOnly { get; init; } = true;
    public decimal MaxDiscountPercentWithoutPermission { get; init; } = 10m;
    public string ReceiptHeader { get; init; } = "";
    public string ReceiptFooter { get; init; } = "";
    public int ReceiptWidthMm { get; init; } = 80;
    public int AutoLockMinutes { get; init; } = 5;
    public int SyncIntervalSeconds { get; init; } = 30;
    public string Theme { get; init; } = "dark";
    public string OrderNoPrefix { get; init; } = "ORD";
    public bool RoundTotalToTaka { get; init; } = true;
    public bool VatAppliesToServiceCharge { get; init; } = true;
    public decimal DeliveryChargeDefault { get; init; } = 60m;
    public IReadOnlyList<string> MobileBankingProviders { get; init; } = ["bKash", "Nagad", "Rocket", "Upay"];
}

public sealed class RestaurantSettingsValidator : AbstractValidator<RestaurantSettings>
{
    public RestaurantSettingsValidator()
    {
        RuleFor(x => x.RestaurantName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.RestaurantPhone).MaximumLength(30);
        RuleFor(x => x.VatRate).InclusiveBetween(0, 100);
        RuleFor(x => x.ServiceChargeRate).InclusiveBetween(0, 100);
        RuleFor(x => x.MaxDiscountPercentWithoutPermission).InclusiveBetween(0, 100);
        RuleFor(x => x.ReceiptWidthMm).Must(w => w is 58 or 80).WithMessage("Receipt width must be 58 or 80 mm");
        RuleFor(x => x.AutoLockMinutes).InclusiveBetween(1, 120);
        RuleFor(x => x.SyncIntervalSeconds).InclusiveBetween(10, 3600);
        RuleFor(x => x.ReceiptHeader).MaximumLength(500);
        RuleFor(x => x.ReceiptFooter).MaximumLength(500);
        RuleFor(x => x.Theme).Must(t => t is "dark" or "light");
    }
}

public sealed class SettingsService(ISettingsRepository repo, ICurrentSession session, IDeviceContext device, IClock clock)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly RestaurantSettingsValidator _validator = new();
    private RestaurantSettings? _cache;

    public event EventHandler<RestaurantSettings>? SettingsChanged;

    /// <summary>Called after settings arrive from the server (pull sync): reload and notify listeners.</summary>
    public async Task ReloadAfterSyncAsync(CancellationToken ct = default)
    {
        _cache = null;
        var fresh = await GetAsync(true, ct);
        SettingsChanged?.Invoke(this, fresh);
    }

    public async Task<RestaurantSettings> GetAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (_cache is not null && !refresh) return _cache;
        var rows = (await repo.GetEffectiveAsync(device.BranchUuid, ct)).ToDictionary(r => r.SettingKey, r => r.SettingValue);
        string S(string k, string d) => rows.TryGetValue(k, out var v) && v is not null ? v : d;
        decimal D(string k, decimal d) => decimal.TryParse(S(k, ""), NumberStyles.Number, Inv, out var x) ? x : d;
        int I(string k, int d) => int.TryParse(S(k, ""), NumberStyles.Integer, Inv, out var x) ? x : d;
        bool B(string k, bool d) => bool.TryParse(S(k, ""), out var x) ? x : d;
        _cache = new RestaurantSettings
        {
            RestaurantName = S("restaurant_name", "Darbar_E_Saltanat"),
            RestaurantAddress = S("restaurant_address", ""), RestaurantPhone = S("restaurant_phone", ""),
            CurrencySymbol = S("currency_symbol", "৳"),
            VatRate = D("vat_rate", 5), VatInclusive = B("vat_inclusive", false),
            ServiceChargeRate = D("service_charge_rate", 10), ServiceChargeDineInOnly = B("service_charge_dine_in_only", true),
            MaxDiscountPercentWithoutPermission = D("max_discount_percent_without_permission", 10),
            ReceiptHeader = S("receipt_header", ""), ReceiptFooter = S("receipt_footer", ""),
            ReceiptWidthMm = I("receipt_width_mm", 80), AutoLockMinutes = I("auto_lock_minutes", 5),
            SyncIntervalSeconds = I("sync_interval_seconds", 30), Theme = S("theme", "dark"),
            OrderNoPrefix = S("order_no_prefix", "ORD"),
            RoundTotalToTaka = B("round_total_to_taka", true),
            VatAppliesToServiceCharge = B("vat_applies_to_service_charge", true),
            DeliveryChargeDefault = D("delivery_charge_default", 60),
            MobileBankingProviders = S("mobile_banking_providers", "bKash,Nagad,Rocket,Upay")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        return _cache;
    }

    /// <summary>Saves locally first (offline-safe). The change is queued in sync_outbox atomically.</summary>
    public async Task<Result> SaveAsync(RestaurantSettings s, CancellationToken ct = default)
    {
        var user = session.Require(Permissions.SettingsManage);
        var v = _validator.Validate(s);
        if (!v.IsValid) return Result.Fail(string.Join(Environment.NewLine, v.Errors.Select(e => e.ErrorMessage)), "VALIDATION");

        var old = await GetAsync(true, ct);
        var values = new Dictionary<string, (string? Value, string Type)>
        {
            ["restaurant_name"] = (s.RestaurantName.Trim(), "STRING"),
            ["restaurant_address"] = (s.RestaurantAddress?.Trim(), "STRING"),
            ["restaurant_phone"] = (s.RestaurantPhone?.Trim(), "STRING"),
            ["vat_rate"] = (s.VatRate.ToString(Inv), "NUMBER"),
            ["vat_inclusive"] = (s.VatInclusive ? "true" : "false", "BOOL"),
            ["service_charge_rate"] = (s.ServiceChargeRate.ToString(Inv), "NUMBER"),
            ["service_charge_dine_in_only"] = (s.ServiceChargeDineInOnly ? "true" : "false", "BOOL"),
            ["max_discount_percent_without_permission"] = (s.MaxDiscountPercentWithoutPermission.ToString(Inv), "NUMBER"),
            ["receipt_header"] = (s.ReceiptHeader, "STRING"),
            ["receipt_footer"] = (s.ReceiptFooter, "STRING"),
            ["receipt_width_mm"] = (s.ReceiptWidthMm.ToString(Inv), "NUMBER"),
            ["auto_lock_minutes"] = (s.AutoLockMinutes.ToString(Inv), "NUMBER"),
            ["sync_interval_seconds"] = (s.SyncIntervalSeconds.ToString(Inv), "NUMBER"),
            ["theme"] = (s.Theme, "STRING"),
        };
        var audit = new AuditLog
        {
            UserUuid = user.UserUuid, Action = "settings.update", EntityType = "settings", OccurredAt = clock.UtcNow,
            BranchUuid = device.BranchUuid, OldValues = JsonSerializer.Serialize(old), NewValues = JsonSerializer.Serialize(s),
            UserAgent = $"desktop:{device.DeviceId}",
        };
        // Save settings for this branch (matching the seed branch).
        await repo.UpsertAsync(device.BranchUuid, values, audit, ct);
        _cache = null;
        var fresh = await GetAsync(true, ct);
        SettingsChanged?.Invoke(this, fresh);
        return Result.Ok();
    }
}
