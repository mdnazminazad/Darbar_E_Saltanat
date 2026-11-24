using System.Text;

namespace DarbarESaltanat.Application.Menu;

/// <summary>RFC 4180 CSV (quoted fields, embedded commas/quotes/newlines). UTF-8 with BOM so Excel shows Bangla correctly.</summary>
public static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; break;
                case '﻿' when i == 0: break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows.Where(r => r.Any(f => f.Length > 0)).ToList();
    }

    public static string Escape(string? v)
    {
        v ??= "";
        // Neutralise spreadsheet formula injection.
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' && !decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _)) v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string Write(IEnumerable<IEnumerable<string?>> rows) =>
        "﻿" + string.Join("\r\n", rows.Select(r => string.Join(",", r.Select(Escape)))) + "\r\n";
}
