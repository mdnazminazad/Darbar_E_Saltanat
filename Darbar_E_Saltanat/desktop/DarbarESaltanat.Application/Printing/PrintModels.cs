namespace DarbarESaltanat.Application.Printing;

public enum PrintTarget { Receipt, Kitchen }

/// <summary>A ready-to-send document: raw ESC/POS bytes plus a plain-text rendering for on-screen preview.</summary>
public sealed record PrintJob(PrintTarget Target, string Title, string PreviewText, byte[] Data, string? Station = null)
{
    /// <summary>Show the document on screen first and print only when the cashier presses Print (customer bills/receipts always do).</summary>
    public bool PreviewFirst { get; init; }
}

/// <summary>Sends print jobs to the configured printers (implemented in the desktop app with the Windows spooler).</summary>
public interface IPrintService
{
    /// <returns>null on success, otherwise a user-facing error (the order is already saved — the user can reprint).</returns>
    Task<string?> PrintAsync(PrintJob job, CancellationToken ct = default);
    Task<string?> OpenCashDrawerAsync(CancellationToken ct = default);
}

public enum TextAlign { Left, Center, Right }

public sealed record ReceiptLine(string Text, TextAlign Align = TextAlign.Left, bool Bold = false, bool Large = false)
{
    public static ReceiptLine Separator(char c = '-') => new(new string(c, 1)) { IsSeparator = true, SeparatorChar = c };
    public bool IsSeparator { get; init; }
    public char SeparatorChar { get; init; } = '-';
}
