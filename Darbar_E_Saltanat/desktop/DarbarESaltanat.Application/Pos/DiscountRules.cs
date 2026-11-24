using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;

namespace DarbarESaltanat.Application.Pos;

/// <summary>What the cashier asked for. Exactly one of Value / CouponCode / PresetUuid is used depending on Type.</summary>
public sealed record DiscountRequest(string Type, decimal Value = 0, string? CouponCode = null, string? PresetUuid = null)
{
    public static DiscountRequest Clear() => new(DiscountType.None);
}

public sealed record CouponCheck(bool Valid, string? Reason, decimal DiscountAmount)
{
    public static CouponCheck Fail(string reason) => new(false, reason, 0);
}

/// <summary>Coupon validity (mirrors web/server/src/modules/catalog/couponRules.js).</summary>
public static class CouponRules
{
    /// <param name="localUsageCount">coupon_usages recorded on this device that the server has not counted yet.</param>
    public static CouponCheck Evaluate(Coupon? c, decimal subtotal, DateTime nowUtc, bool hasCustomer, int customerUsageCount, int localUsageCount)
    {
        if (c is null || !c.IsActive || c.IsDeleted) return CouponCheck.Fail("Coupon not found or inactive");
        if (c.StartsAt is { } s && s > nowUtc) return CouponCheck.Fail("Coupon is not active yet");
        if (c.EndsAt is { } e && e < nowUtc) return CouponCheck.Fail("Coupon has expired");
        if (subtotal < c.MinOrder) return CouponCheck.Fail($"Minimum order for this coupon is {Money.Format(c.MinOrder)}");
        if (c.UsageLimit is { } limit && c.UsedCount + localUsageCount >= limit) return CouponCheck.Fail("Coupon usage limit reached");
        if (c.PerCustomerLimit is { } per)
        {
            if (!hasCustomer) return CouponCheck.Fail("Select the customer first — this coupon is limited per customer");
            if (customerUsageCount >= per) return CouponCheck.Fail("This customer has already used the coupon");
        }
        var amount = PricingEngine.DiscountAmount(new OrderDiscount(c.Type, c.Value, c.MaxDiscount), subtotal);
        return new CouponCheck(true, null, amount);
    }

    public static OrderDiscount ToOrderDiscount(Coupon c) => new(c.Type, c.Value, c.MaxDiscount);
}
