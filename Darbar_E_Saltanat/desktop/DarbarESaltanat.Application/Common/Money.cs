using System.Globalization;

namespace DarbarESaltanat.Application.Common;

public static class Money
{
    /// <summary>Round to paisa (2 dp), half away from zero — the convention on Bangladeshi receipts.</summary>
    public static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    public static decimal RoundTaka(decimal v) => Math.Round(v, 0, MidpointRounding.AwayFromZero);
    public static string Format(decimal v, string symbol = "৳") => $"{symbol}{v.ToString("#,##0.00", CultureInfo.InvariantCulture)}";
    /// <summary>Plain-ASCII amount for thermal printers.</summary>
    public static string Plain(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
    public static bool HasAtMostTwoDecimals(decimal v) => Round2(v) == v;
}

/// <summary>Asia/Dhaka (UTC+6, no DST). Databases store UTC; this is for display, business dates and order numbers.</summary>
public static class Dhaka
{
    public static readonly TimeZoneInfo Zone =
        TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Dhaka", out var tz) ? tz
        : TimeZoneInfo.TryFindSystemTimeZoneById("Bangladesh Standard Time", out tz) ? tz
        : TimeZoneInfo.CreateCustomTimeZone("Asia/Dhaka", TimeSpan.FromHours(6), "Asia/Dhaka", "Asia/Dhaka");

    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>UTC start of the Dhaka calendar day containing <paramref name="utc"/>.</summary>
    public static DateTime StartOfDayUtc(DateTime utc)
    {
        var local = ToLocal(utc).Date;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
    }
}
