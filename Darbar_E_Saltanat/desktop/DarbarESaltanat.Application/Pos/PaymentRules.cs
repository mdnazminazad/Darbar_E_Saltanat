using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;

namespace DarbarESaltanat.Application.Pos;

/// <param name="Amount">Amount to apply to the bill.</param>
/// <param name="Tendered">Cash handed over (cash only; may exceed Amount → change).</param>
/// <param name="Provider">Mobile banking provider (bKash, Nagad, …) or card network.</param>
/// <param name="Reference">Transaction ID / card slip number.</param>
public sealed record PaymentRequest(string Method, decimal Amount, decimal? Tendered = null, string? Provider = null, string? Reference = null);

public sealed record PaymentCheck(bool Valid, string? Error, decimal Change)
{
    public static PaymentCheck Fail(string e) => new(false, e, 0);
}

public static class PaymentRules
{
    public static PaymentCheck Validate(PaymentRequest p, decimal due, IReadOnlyCollection<string> mobileProviders, Customer? customer)
    {
        if (!PaymentMethod.All.Contains(p.Method)) return PaymentCheck.Fail("Choose a payment method");
        if (due <= 0) return PaymentCheck.Fail("Nothing is due on this order");
        if (p.Amount <= 0) return PaymentCheck.Fail("Amount must be greater than zero");
        if (!Money.HasAtMostTwoDecimals(p.Amount)) return PaymentCheck.Fail("Amount can have at most 2 decimal places");
        if (p.Amount > due) return PaymentCheck.Fail($"Amount is more than the due {Money.Format(due)}. For cash, enter the cash received as 'Tendered'.");

        var change = 0m;
        switch (p.Method)
        {
            case PaymentMethod.Cash:
                var tendered = p.Tendered ?? p.Amount;
                if (tendered < p.Amount) return PaymentCheck.Fail("Cash received is less than the amount");
                if (!Money.HasAtMostTwoDecimals(tendered)) return PaymentCheck.Fail("Cash received can have at most 2 decimal places");
                change = tendered - p.Amount;
                break;
            case PaymentMethod.MobileBanking:
                if (string.IsNullOrWhiteSpace(p.Provider) || !mobileProviders.Contains(p.Provider, StringComparer.OrdinalIgnoreCase))
                    return PaymentCheck.Fail($"Choose the mobile banking provider ({string.Join(", ", mobileProviders)})");
                if (string.IsNullOrWhiteSpace(p.Reference) || p.Reference.Trim().Length < 6)
                    return PaymentCheck.Fail("Enter the transaction ID (TrxID) from the customer's confirmation SMS");
                break;
            case PaymentMethod.Card:
                if (p.Reference is { Length: > 40 }) return PaymentCheck.Fail("Card reference is too long");
                break;
            case PaymentMethod.Credit:
                if (customer is null) return PaymentCheck.Fail("Select a customer to sell on credit");
                if (customer.CreditLimit <= 0) return PaymentCheck.Fail($"{customer.Name} is not allowed credit (credit limit is zero)");
                var available = customer.CreditLimit - customer.CreditBalance;
                if (p.Amount > available) return PaymentCheck.Fail($"Exceeds available credit {Money.Format(Math.Max(0, available))}");
                break;
        }
        if (p.Method != PaymentMethod.Cash && p.Tendered is not null && p.Tendered != p.Amount)
            return PaymentCheck.Fail("Only cash payments can return change");
        return new PaymentCheck(true, null, change);
    }
}
