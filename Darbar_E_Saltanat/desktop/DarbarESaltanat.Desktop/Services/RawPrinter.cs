using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DarbarESaltanat.Desktop.Services;

/// <summary>Sends raw ESC/POS bytes to a Windows printer queue (works for USB, network and shared thermal printers).</summary>
internal static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName = "";
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW";
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr handle, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DocInfo info);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, byte[] bytes, int count, out int written);

    private static readonly string[] SoftwarePrinterHints = ["xps document writer", "print to pdf", "onenote", "fax", "pdf"];
    private static readonly string[] ThermalHints = ["rongta", "thermal", "receipt", "pos", "80mm", "58mm", "xprinter", "epson tm", "tm-"];

    /// <summary>True for Windows software printers (XPS / PDF / OneNote / Fax). They cannot take raw ESC/POS data, so a receipt can never print on them.</summary>
    public static bool IsVirtual(string? printerName) =>
        printerName is not null && SoftwarePrinterHints.Any(h => printerName.Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>The installed thermal / receipt printer (never a software printer). Null when none is installed.</summary>
    public static string? FindThermal() =>
        Installed().Where(n => !IsVirtual(n)).FirstOrDefault(n => ThermalHints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The printer a job should really use. The saved one when it is a real, installed printer; when the saved one is a
    /// software printer (or no longer installed) the thermal printer installed on this PC; null when nothing is saved
    /// or no thermal printer exists.
    /// </summary>
    public static string? UsablePrinter(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (IsVirtual(configured)) return FindThermal();
        var installed = Installed();
        if (installed.Count == 0 || installed.Contains(configured, StringComparer.OrdinalIgnoreCase)) return configured;
        return FindThermal() ?? configured;
    }

    public static void Send(string printerName, byte[] data, string documentName)
    {
        if (IsVirtual(printerName))
            throw new InvalidOperationException($"'{printerName}' is a software printer (XPS / PDF / OneNote / Fax) and cannot print 80 mm receipts. Choose your thermal receipt printer in Settings → Printers.");
        if (!OpenPrinter(printerName, out var h, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open printer '{printerName}'");
        try
        {
            if (StartDocPrinter(h, 1, new DocInfo { DocName = documentName }) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter failed");
            try
            {
                if (!StartPagePrinter(h)) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter failed");
                if (!WritePrinter(h, data, data.Length, out var written) || written != data.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter failed");
                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }

    /// <summary>Installed printer queue names (local + connected network printers).</summary>
    public static IReadOnlyList<string> Installed()
    {
        try
        {
            using var server = new System.Printing.LocalPrintServer();
            using var queues = server.GetPrintQueues([System.Printing.EnumeratedPrintQueueTypes.Local, System.Printing.EnumeratedPrintQueueTypes.Connections]);
            return queues.Select(q => q.FullName).OrderBy(n => n).ToList();
        }
        catch (Exception) { return []; }
    }
}
