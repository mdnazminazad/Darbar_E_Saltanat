using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Enums;

namespace DarbarESaltanat.Application.Pos;

/// <summary>One priced line. <paramref name="AddonsPerUnit"/> is the add-on price for ONE unit of the item.</summary>
public sealed record PricingLine(string Key, decimal UnitPrice, decimal AddonsPerUnit, int Quantity, decimal LineDiscount, decimal TaxRate);

/// <summary>Order-level discount. PERCENT/FIXED; coupons and presets are resolved into one of these.</summary>
public sealed record OrderDiscount(string Type, decimal Value, decimal? MaxDiscount = null)
{
    public static readonly OrderDiscount None = new(DiscountType.None, 0);
}

public sealed record PricingOptions(
    decimal ServiceChargeRate,
    bool VatInclusive,
    bool VatOnServiceCharge,
    decimal DeliveryCharge,
    bool RoundToTaka);

public sealed record PricedLine(string Key, decimal LineTotal, decimal DiscountShare, decimal ServiceChargeShare, decimal Vat);

public sealed record PricingResult(
    decimal Subtotal, decimal DiscountAmount, decimal ServiceCharge, decimal Vat,
    decimal DeliveryCharge, decimal RoundOff, decimal Total, IReadOnlyList<PricedLine> Lines);

/// <summary>
/// Pure, deterministic order calculator (no I/O) — the single place where money is computed on the desktop.
/// <code>
/// line total      = (unit price + add-ons per unit) × qty − line discount
/// subtotal        = Σ line totals
/// discount        = PERCENT: subtotal × v% (capped by max) | FIXED: min(v, subtotal)
/// service charge  = (subtotal − discount) × SC%
/// VAT (exclusive) = Σ per-line (line − its discount share + its SC share*) × line VAT%     *if VAT applies to SC
/// VAT (inclusive) = the same base × r / (100 + r) — already inside the prices, not added again
/// total           = subtotal − discount + SC + VAT(exclusive only) + delivery, optionally rounded to whole taka
/// </code>
/// Discount and service charge are allocated to lines in proportion to line totals, to the paisa, so mixed VAT
/// rates are exact and the per-line figures always add up to the order figures.
/// </summary>
public static class PricingEngine
{
    public static PricingResult Calculate(IReadOnlyList<PricingLine> lines, OrderDiscount discount, PricingOptions opt)
    {
        ArgumentNullException.ThrowIfNull(lines);
        Validate(lines, discount, opt);

        var lineTotals = lines.Select(l => Money.Round2((l.UnitPrice + l.AddonsPerUnit) * l.Quantity - l.LineDiscount)).ToArray();
        var subtotal = lineTotals.Sum();

        var discountAmount = DiscountAmount(discount, subtotal);
        var serviceCharge = Money.Round2((subtotal - discountAmount) * opt.ServiceChargeRate / 100m);

        var discShares = Allocate(discountAmount, lineTotals);
        var scShares = Allocate(serviceCharge, lineTotals);

        var rawVat = new decimal[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var baseAmount = lineTotals[i] - discShares[i] + (opt.VatOnServiceCharge ? scShares[i] : 0m);
            var r = lines[i].TaxRate;
            rawVat[i] = opt.VatInclusive ? baseAmount * r / (100m + r) : baseAmount * r / 100m;
        }
        var vat = Money.Round2(rawVat.Sum());
        var lineVat = Allocate(vat, rawVat); // per-line VAT that sums exactly to the order VAT

        var delivery = Money.Round2(opt.DeliveryCharge);
        var raw = subtotal - discountAmount + serviceCharge + (opt.VatInclusive ? 0m : vat) + delivery;
        var total = opt.RoundToTaka ? Money.RoundTaka(raw) : Money.Round2(raw);
        var roundOff = total - raw;

        var priced = lines.Select((l, i) => new PricedLine(l.Key, lineTotals[i], discShares[i], scShares[i], lineVat[i])).ToList();
        return new PricingResult(subtotal, discountAmount, serviceCharge, vat, delivery, roundOff, total, priced);
    }

    public static decimal DiscountAmount(OrderDiscount d, decimal subtotal)
    {
        if (subtotal <= 0) return 0;
        var amount = d.Type switch
        {
            DiscountType.Percent => subtotal * d.Value / 100m,
            DiscountType.Fixed => Math.Min(d.Value, subtotal),
            _ => 0m,
        };
        if (d.MaxDiscount is { } cap) amount = Math.Min(amount, cap);
        return Money.Round2(Math.Max(0, Math.Min(amount, subtotal)));
    }

    /// <summary>Splits <paramref name="amount"/> across weights to the paisa; any rounding remainder goes to the largest weight.</summary>
    public static decimal[] Allocate(decimal amount, IReadOnlyList<decimal> weights)
    {
        var result = new decimal[weights.Count];
        if (amount == 0 || weights.Count == 0) return result;
        var total = weights.Sum();
        if (total == 0) { result[0] = amount; return result; }
        for (var i = 0; i < weights.Count; i++) result[i] = Money.Round2(amount * weights[i] / total);
        var diff = amount - result.Sum();
        if (diff != 0)
        {
            var largest = 0;
            for (var i = 1; i < weights.Count; i++) if (weights[i] > weights[largest]) largest = i;
            result[largest] += diff;
        }
        return result;
    }

    private static void Validate(IReadOnlyList<PricingLine> lines, OrderDiscount d, PricingOptions opt)
    {
        foreach (var l in lines)
        {
            if (l.Quantity <= 0) throw new ArgumentException($"Quantity must be positive ({l.Key})");
            if (l.UnitPrice < 0 || l.AddonsPerUnit < 0 || l.LineDiscount < 0) throw new ArgumentException($"Negative price on line {l.Key}");
            if (l.TaxRate is < 0 or > 100) throw new ArgumentException($"Invalid tax rate on line {l.Key}");
            if (l.LineDiscount > (l.UnitPrice + l.AddonsPerUnit) * l.Quantity) throw new ArgumentException($"Line discount exceeds line amount ({l.Key})");
        }
        if (d.Value < 0) throw new ArgumentException("Discount cannot be negative");
        if (d.Type == DiscountType.Percent && d.Value > 100) throw new ArgumentException("Discount percentage cannot exceed 100");
        if (opt.ServiceChargeRate is < 0 or > 100) throw new ArgumentException("Invalid service charge rate");
        if (opt.DeliveryCharge < 0) throw new ArgumentException("Delivery charge cannot be negative");
    }
}
