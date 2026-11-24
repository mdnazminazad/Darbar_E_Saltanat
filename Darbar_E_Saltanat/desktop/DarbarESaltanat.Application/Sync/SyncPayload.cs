using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using DarbarESaltanat.Domain.Common;
using DarbarESaltanat.Domain.Entities;

namespace DarbarESaltanat.Application.Sync;

/// <summary>
/// The wire format of one row, shared by the outbox (AppDbContext) and the sync engine:
/// snake_case columns, foreign keys as *_uuid, ISO-8601 UTC timestamps, decimals as invariant strings,
/// booleans as true/false. The local <c>id</c> and <c>sync_status</c> never leave the device.
/// </summary>
public static class SyncPayload
{
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Prop, string Column)[]> Cache = new();

    public static string Table(Type entityType) =>
        entityType.GetCustomAttribute<SyncTableAttribute>()?.Name ?? throw new InvalidOperationException($"{entityType.Name} has no [SyncTable]");

    public static string Snake(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c)) { if (i > 0) sb.Append('_'); sb.Append(char.ToLowerInvariant(c)); }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string FormatUtc(DateTime d) =>
        (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d).ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static (PropertyInfo Prop, string Column)[] Columns(Type t) => Cache.GetOrAdd(t, type => type.GetProperties()
        .Where(p => p.CanWrite && p.CanRead)
        .Select(p => (p, Snake(p.Name)))
        .Where(x => x.Item2 is not ("id" or "sync_status") && !(type == typeof(Branch) && x.Item2 == "branch_uuid"))
        .ToArray());

    public static Dictionary<string, object?> Snapshot(SyncEntity entity)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (prop, column) in Columns(entity.GetType()))
        {
            d[column] = prop.GetValue(entity) switch
            {
                DateTime dt => FormatUtc(dt),
                decimal m => m.ToString(CultureInfo.InvariantCulture),
                var v => v,
            };
        }
        return d;
    }
}
