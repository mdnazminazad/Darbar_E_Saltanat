using System.Text;

namespace DarbarESaltanat.Application.Printing;

/// <summary>Minimal ESC/POS encoder for 58 mm (32 columns) and 80 mm (48 columns) thermal printers.</summary>
public static class EscPos
{
    private const byte Esc = 0x1B, Gs = 0x1D;

    public static int Columns(int paperWidthMm) => paperWidthMm <= 58 ? 32 : 48;

    /// <summary>Thermal printers' default code page is ASCII-compatible; Bangla and ৳ are replaced.</summary>
    public static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Replace("৳", "Tk ").Replace('—', '-').Replace('–', '-').Replace('’', '\''))
            sb.Append(ch is >= ' ' and <= '~' ? ch : '?');
        return sb.ToString();
    }

    public static byte[] Render(IEnumerable<ReceiptLine> lines, int columns, bool cut = true, bool openDrawer = false)
    {
        var buf = new List<byte> { Esc, 0x40 }; // initialise
        foreach (var l in lines)
        {
            var width = l.Large ? columns / 2 : columns;
            buf.AddRange([Esc, 0x61, l.Align switch { TextAlign.Center => (byte)1, TextAlign.Right => (byte)2, _ => (byte)0 }]);
            buf.AddRange([Esc, 0x45, l.Bold ? (byte)1 : (byte)0]);
            buf.AddRange([Gs, 0x21, l.Large ? (byte)0x11 : (byte)0x00]);
            var text = l.IsSeparator ? new string(l.SeparatorChar, width) : Sanitize(l.Text);
            foreach (var row in Wrap(text, width)) { buf.AddRange(Encoding.ASCII.GetBytes(row)); buf.Add(0x0A); }
        }
        buf.AddRange([Gs, 0x21, 0x00, Esc, 0x45, 0x00, Esc, 0x61, 0x00]);
        buf.AddRange([Esc, 0x64, 4]); // feed 4 lines
        if (cut) buf.AddRange([Gs, 0x56, 0x42, 0x00]);
        if (openDrawer) buf.AddRange(DrawerKick);
        return buf.ToArray();
    }

    /// <summary>ESC p 0 25 250 — pulse pin 2 (standard RJ11 cash drawer port).</summary>
    public static readonly byte[] DrawerKick = [Esc, 0x70, 0x00, 0x19, 0xFA];

    /// <summary>Plain-text preview with the same wrapping and alignment as the printer.</summary>
    public static string Preview(IEnumerable<ReceiptLine> lines, int columns)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            var text = l.IsSeparator ? new string(l.SeparatorChar, columns) : Sanitize(l.Text);
            // Large text is double width on paper (half the columns); on screen it is shown upper-case.
            var width = l.Large ? columns / 2 : columns;
            foreach (var row in Wrap(l.Large ? text.ToUpperInvariant() : text, width))
            {
                var shown = row;
                sb.AppendLine(l.Align switch
                {
                    TextAlign.Center => shown.PadLeft((columns + shown.Length) / 2).PadRight(columns),
                    TextAlign.Right => shown.PadLeft(columns),
                    _ => shown,
                });
            }
        }
        return sb.ToString();
    }

    public static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length == 0) { yield return ""; yield break; }
        foreach (var para in text.Split('\n'))
        {
            var rest = para.TrimEnd();
            if (rest.Length == 0) { yield return ""; continue; }
            while (rest.Length > width)
            {
                var cut = rest.LastIndexOf(' ', width);
                if (cut <= 0) cut = width;
                yield return rest[..cut].TrimEnd();
                rest = rest[cut..].TrimStart();
            }
            yield return rest;
        }
    }

    /// <summary>"Kacchi Biryani x2 ........ 900.00" — left text wrapped, right text on the last row.</summary>
    public static string Columns2(string left, string right, int width)
    {
        left = Sanitize(left); right = Sanitize(right);
        var room = width - right.Length - 1;
        if (room < 8) return left + "\n" + right.PadLeft(width);
        var rows = Wrap(left, room).ToList();
        var last = rows[^1];
        rows[^1] = last.PadRight(room) + " " + right;
        return string.Join("\n", rows);
    }
}
