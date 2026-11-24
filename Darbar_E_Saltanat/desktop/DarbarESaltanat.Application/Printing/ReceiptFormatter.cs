using System.Globalization;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;

namespace DarbarESaltanat.Application.Printing;

public enum ReceiptKind { Bill, Receipt, Reprint, Refund }

/// <summary>Builds customer bills/receipts and kitchen order tickets (KOT) for 58/80 mm printers.</summary>
public static class ReceiptFormatter
{
    private static string T(decimal v) => Money.Plain(v);
    private static string Local(DateTime utc) => Dhaka.ToLocal(utc).ToString("dd-MMM-yyyy hh:mm tt", CultureInfo.InvariantCulture);

    public static PrintJob ForCustomer(OrderView v, RestaurantSettings s, ReceiptKind kind, string? cashierName, DateTime nowUtc, decimal change = 0, Refund? refund = null)
    {
        var cols = EscPos.Columns(s.ReceiptWidthMm);
        var o = v.Order;
        var lines = new List<ReceiptLine>
        {
            new(s.RestaurantName, TextAlign.Center, Bold: true, Large: true),
        };
        if (!string.IsNullOrWhiteSpace(s.RestaurantAddress)) lines.Add(new(s.RestaurantAddress!, TextAlign.Center));
        if (!string.IsNullOrWhiteSpace(s.RestaurantPhone)) lines.Add(new($"Phone: {s.RestaurantPhone}", TextAlign.Center));
        if (!string.IsNullOrWhiteSpace(s.ReceiptHeader)) lines.Add(new(s.ReceiptHeader, TextAlign.Center));
        lines.Add(ReceiptLine.Separator('='));
        lines.Add(new(kind switch
        {
            ReceiptKind.Bill => "BILL",
            ReceiptKind.Refund => "REFUND SLIP",
            ReceiptKind.Reprint => "RECEIPT (COPY)",
            _ => "RECEIPT",
        }, TextAlign.Center, Bold: true));
        lines.Add(new($"Order: {o.OrderNo}"));
        lines.Add(new($"Date : {Local(o.OrderDate)}"));
        lines.Add(new($"Type : {OrderType.Label(o.OrderType)}{(v.TableName is null ? "" : $"   Table: {v.TablesLabel}")}"));
        if (o.OrderType == OrderType.DineIn) lines.Add(new($"Guests: {o.GuestCount}"));
        if (v.WaiterName is not null) lines.Add(new($"Served by: {v.WaiterName}"));
        if (v.Customer is not null) lines.Add(new($"Customer: {v.Customer.Name} ({v.Customer.Phone})"));
        if (o.OrderType == OrderType.Delivery && !string.IsNullOrWhiteSpace(o.DeliveryAddress)) lines.Add(new($"Deliver to: {o.DeliveryAddress} {o.DeliveryPhone}"));
        lines.Add(ReceiptLine.Separator());
        lines.Add(new(EscPos.Columns2("Item", "Amount", cols), Bold: true));
        foreach (var l in v.Lines.Where(x => !x.IsCancelled))
        {
            lines.Add(new(EscPos.Columns2($"{l.Name} x{l.Quantity} @{T(l.UnitPrice + l.AddonsPerUnit)}", T(l.LineTotal), cols)));
            if (l.AddonsText is not null) lines.Add(new($"  + {l.AddonsText}"));
        }
        lines.Add(ReceiptLine.Separator());
        void Row(string label, decimal amount, bool bold = false) => lines.Add(new(EscPos.Columns2(label, T(amount), cols), Bold: bold));
        Row("Subtotal", o.Subtotal);
        if (o.DiscountAmount > 0)
            Row(o.DiscountType switch
            {
                DiscountType.Percent => $"Discount ({o.DiscountValue:0.##}%)",
                DiscountType.Coupon => $"Coupon {v.CouponCode}",
                _ => "Discount",
            }, -o.DiscountAmount);
        if (o.ServiceChargeAmount > 0) Row($"Service charge ({o.ServiceChargeRate:0.##}%)", o.ServiceChargeAmount);
        if (o.VatAmount > 0) Row(s.VatInclusive ? "VAT (included)" : $"VAT", o.VatAmount);
        if (o.DeliveryCharge > 0) Row("Delivery charge", o.DeliveryCharge);
        if (o.RoundOff != 0) Row("Round off", o.RoundOff);
        lines.Add(ReceiptLine.Separator('='));
        lines.Add(new(EscPos.Columns2("TOTAL Tk", T(o.Total), cols / 2), Bold: true, Large: true));
        lines.Add(ReceiptLine.Separator('='));

        if (kind != ReceiptKind.Bill)
        {
            foreach (var p in v.Payments)
                Row($"Paid {PaymentMethod.Label(p.Method)}{(p.Provider is null ? "" : $" ({p.Provider})")}{(p.Reference is null ? "" : $" #{p.Reference}")}", p.Amount);
            if (change > 0) Row("Change", change, bold: true);
            foreach (var r in v.Refunds) Row($"Refunded ({PaymentMethod.Label(r.Method)})", -r.Amount);
        }
        if (o.DueAmount > 0) Row("AMOUNT DUE", o.DueAmount, bold: true);
        if (refund is not null)
        {
            lines.Add(ReceiptLine.Separator());
            Row($"REFUND {PaymentMethod.Label(refund.Method)}", refund.Amount, bold: true);
            lines.Add(new($"Reason: {refund.Reason}"));
        }
        lines.Add(ReceiptLine.Separator());
        if (cashierName is not null) lines.Add(new($"Cashier: {cashierName}"));
        lines.Add(new($"Printed: {Local(nowUtc)}"));
        if (!string.IsNullOrWhiteSpace(s.ReceiptFooter)) lines.Add(new(s.ReceiptFooter, TextAlign.Center, Bold: true));
        lines.Add(new("Prices in BDT (Tk)", TextAlign.Center));

        var title = $"{kind} {o.OrderNo}";
        return new PrintJob(PrintTarget.Receipt, title, EscPos.Preview(lines, cols), EscPos.Render(lines, cols));
    }

    public sealed record KotLine(string Name, int Quantity, string? Addons, string? Notes);

    public static PrintJob Kot(string kotNo, string? station, OrderView v, IReadOnlyList<KotLine> items, RestaurantSettings s, DateTime nowUtc, bool cancellation = false)
    {
        var cols = EscPos.Columns(s.ReceiptWidthMm);
        var o = v.Order;
        var lines = new List<ReceiptLine>
        {
            new(cancellation ? "*** CANCEL ***" : "KITCHEN ORDER", TextAlign.Center, Bold: true, Large: true),
            new(station ?? "Kitchen", TextAlign.Center, Bold: true),
            ReceiptLine.Separator('='),
            new($"KOT  : {kotNo}", Bold: true),
            new($"Order: {o.OrderNo}"),
            new($"{OrderType.Label(o.OrderType)}{(v.TableName is null ? "" : $"  TABLE {v.TablesLabel}")}", Bold: true, Large: v.TableName is not null),
            new($"Time : {Local(nowUtc)}"),
        };
        if (v.WaiterName is not null) lines.Add(new($"Waiter: {v.WaiterName}"));
        lines.Add(ReceiptLine.Separator());
        foreach (var i in items)
        {
            lines.Add(new($"{i.Quantity} x {i.Name}", Bold: true, Large: true));
            if (i.Addons is not null) lines.Add(new($"   + {i.Addons}"));
            if (!string.IsNullOrWhiteSpace(i.Notes)) lines.Add(new($"   ** {i.Notes}", Bold: true));
        }
        if (!string.IsNullOrWhiteSpace(o.Notes)) { lines.Add(ReceiptLine.Separator()); lines.Add(new($"ORDER NOTE: {o.Notes}", Bold: true)); }
        lines.Add(ReceiptLine.Separator());
        return new PrintJob(PrintTarget.Kitchen, $"{(cancellation ? "Cancel " : "")}KOT {kotNo}", EscPos.Preview(lines, cols), EscPos.Render(lines, cols), station);
    }
}
