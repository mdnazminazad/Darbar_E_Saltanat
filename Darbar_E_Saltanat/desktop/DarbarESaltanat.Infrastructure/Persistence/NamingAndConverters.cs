using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DarbarESaltanat.Infrastructure.Persistence;

internal static class Naming
{
    /// <summary>PascalCase → snake_case (BranchUuid → branch_uuid).</summary>
    public static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c)) { if (i > 0) sb.Append('_'); sb.Append(char.ToLowerInvariant(c)); }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>All timestamps are stored as ISO-8601 UTC text, e.g. 2026-11-20T10:00:00.000Z.</summary>
internal static class UtcText
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    public static string Write(DateTime d) => (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d).ToString(Format, CultureInfo.InvariantCulture);
    public static DateTime Read(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static readonly ValueConverter<DateTime, string> Converter = new(d => Write(d), s => Read(s));
    public static readonly ValueConverter<DateTime?, string?> NullableConverter =
        new(d => d.HasValue ? Write(d.Value) : null, s => s == null ? null : Read(s));
}
