using System.Windows;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Desktop.Views;
using DarbarESaltanat.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace DarbarESaltanat.Desktop.Services;

/// <summary>
/// Routes print jobs to the printers configured on THIS computer (Settings → Printers).
/// With no printer configured, the document is shown on screen instead so the shop can keep working.
/// </summary>
public sealed class PrintService(ISyncStateStore state, SettingsService settings, ILogger<PrintService> log) : IPrintService
{
    public async Task<string?> PrintAsync(PrintJob job, CancellationToken ct = default)
    {
        var configured = await ResolvePrinterAsync(job, ct);
        // A saved XPS / PDF / OneNote / Fax printer cannot take raw 80 mm ESC/POS data: print on the thermal printer instead.
        var printer = RawPrinter.UsablePrinter(configured);
        if (job.Target == PrintTarget.Receipt && !string.IsNullOrWhiteSpace(printer) && !string.Equals(printer, configured, StringComparison.OrdinalIgnoreCase))
            await state.SetAsync(SyncStateKeys.ReceiptPrinter, printer, ct);
        if (string.IsNullOrWhiteSpace(printer))
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ReceiptPreviewWindow.Show(job));
            return null;
        }
        try
        {
            await Task.Run(() => RawPrinter.Send(printer, job.Data, job.Title), ct);
            log.LogInformation("Printed {Title} on {Printer}", job.Title, printer);
            return null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Printing {Title} on {Printer} failed", job.Title, printer);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ReceiptPreviewWindow.Show(job));
            return $"Could not print {job.Title} on '{printer}': {ex.Message}. The order is saved — check the printer and use Reprint.";
        }
    }

    public async Task<string?> OpenCashDrawerAsync(CancellationToken ct = default)
    {
        if (await state.GetAsync(SyncStateKeys.CashDrawerEnabled, ct) != "true") return null;
        var printer = RawPrinter.UsablePrinter(await state.GetAsync(SyncStateKeys.ReceiptPrinter, ct));
        if (string.IsNullOrWhiteSpace(printer)) return null;
        try { await Task.Run(() => RawPrinter.Send(printer, EscPos.DrawerKick, "Open cash drawer"), ct); return null; }
        catch (Exception ex) { log.LogError(ex, "Opening cash drawer failed"); return $"Could not open the cash drawer: {ex.Message}"; }
    }

    public async Task<string?> TestAsync(string printerName, CancellationToken ct = default)
    {
        if (RawPrinter.IsVirtual(printerName))
            return $"'{printerName}' is a software printer (XPS / PDF / OneNote / Fax) and cannot print 80 mm receipts. Choose your thermal printer, e.g. RONGTA 80mm Series Printer.";
        var s = await settings.GetAsync(ct: ct);
        var cols = EscPos.Columns(s.ReceiptWidthMm);
        var lines = new[]
        {
            new ReceiptLine(s.RestaurantName, TextAlign.Center, Bold: true, Large: true),
            new ReceiptLine("Printer test", TextAlign.Center),
            ReceiptLine.Separator(),
            new ReceiptLine(EscPos.Columns2("Kacchi Biryani x2", "900.00", cols)),
            new ReceiptLine($"{cols} columns / {s.ReceiptWidthMm} mm paper"),
        };
        try { await Task.Run(() => RawPrinter.Send(printerName, EscPos.Render(lines, cols), "Test page"), ct); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task<string?> ResolvePrinterAsync(PrintJob job, CancellationToken ct)
    {
        if (job.Target == PrintTarget.Receipt) return await state.GetAsync(SyncStateKeys.ReceiptPrinter, ct);
        if (job.Station is not null && await state.GetAsync(SyncStateKeys.KitchenPrinterPrefix + job.Station, ct) is { Length: > 0 } stationPrinter) return stationPrinter;
        return await state.GetAsync(SyncStateKeys.KitchenPrinter, ct) is { Length: > 0 } kitchen ? kitchen : await state.GetAsync(SyncStateKeys.ReceiptPrinter, ct);
    }
}
