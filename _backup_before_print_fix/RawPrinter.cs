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

    public static void Send(string printerName, byte[] data, string documentName)
    {
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
