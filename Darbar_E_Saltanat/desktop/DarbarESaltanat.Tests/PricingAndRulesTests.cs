using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DarbarESaltanat.Tests;

public class PricingEngineTests
{
    private static readonly PricingOptions DineIn = new(ServiceChargeRate: 10, VatInclusive: false, VatOnServiceCharge: true, DeliveryCharge: 0, RoundToTaka: true);
    private static PricingLine L(string k, decimal price, int qty, decimal addons = 0, decimal vat = 5, decimal lineDiscount = 0) => new(k, price, addons, qty, lineDiscount, vat);

    [Fact]
    public void Order_total_with_discount_service_charge_vat_and_rounding()
    {
        // 2 × Kacchi 450 + 2 × Borhani 60 = 1020; 10% off = 102; SC 10% of 918 = 91.80; VAT 5% of 1009.80 = 50.49
        var r = PricingEngine.Calculate([L("kacchi", 450, 2), L("borhani", 60, 2)], new OrderDiscount(DiscountType.Percent, 10), DineIn);
        r.Subtotal.Should().Be(1020m);
        r.DiscountAmount.Should().Be(102m);
        r.ServiceCharge.Should().Be(91.80m);
        r.Vat.Should().Be(50.49m);
        r.Total.Should().Be(1060m);
        r.RoundOff.Should().Be(-0.29m);
        (r.Subtotal - r.DiscountAmount + r.ServiceCharge + r.Vat + r.RoundOff).Should().Be(r.Total);
    }

    [Fact]
    public void Per_line_shares_add_up_exactly_to_order_figures()
    {
        var lines = new[] { L("a", 333.33m, 1), L("b", 99.99m, 3, vat: 15), L("c", 10, 7, addons: 1.5m) };
        var r = PricingEngine.Calculate(lines, new OrderDiscount(DiscountType.Fixed, 77.77m), DineIn);
        r.Lines.Sum(l => l.DiscountShare).Should().Be(r.DiscountAmount);
        r.Lines.Sum(l => l.ServiceChargeShare).Should().Be(r.ServiceCharge);
        r.Lines.Sum(l => l.Vat).Should().Be(r.Vat);
        r.Lines.Single(l => l.Key == "c").LineTotal.Should().Be(80.50m, "add-ons are priced per unit");
    }

    [Fact]
    public void Mixed_vat_rates_are_applied_per_line()
    {
        var noSc = DineIn with { ServiceChargeRate = 0, RoundToTaka = false };
        var r = PricingEngine.Calculate([L("food", 100, 1, vat: 5), L("drink", 100, 1, vat: 15)], OrderDiscount.None, noSc);
        r.Vat.Should().Be(20m);
        r.Total.Should().Be(220m);
    }

    [Fact]
    public void Vat_inclusive_prices_extract_vat_without_adding_it()
    {
        var incl = new PricingOptions(0, VatInclusive: true, VatOnServiceCharge: true, 0, RoundToTaka: false);
        var r = PricingEngine.Calculate([L("x", 105, 1, vat: 5)], OrderDiscount.None, incl);
        r.Vat.Should().Be(5m);
        r.Total.Should().Be(105m);
    }

    [Fact]
    public void Service_charge_can_be_excluded_from_vat_base_and_delivery_is_added()
    {
        var opt = new PricingOptions(10, false, VatOnServiceCharge: false, DeliveryCharge: 60, RoundToTaka: false);
        var r = PricingEngine.Calculate([L("x", 1000, 1)], OrderDiscount.None, opt);
        r.ServiceCharge.Should().Be(100m);
        r.Vat.Should().Be(50m);
        r.Total.Should().Be(1210m);
    }

    [Theory]
    [InlineData(DiscountType.Fixed, 5000, null, 1000)]      // fixed never exceeds subtotal
    [InlineData(DiscountType.Percent, 50, 200.0, 200)]      // percent capped
    [InlineData(DiscountType.Percent, 12.5, null, 125)]
    [InlineData(DiscountType.None, 99, null, 0)]
    public void Discount_calculation(string type, decimal value, double? cap, decimal expected) =>
        PricingEngine.DiscountAmount(new OrderDiscount(type, value, cap is null ? null : (decimal)cap), 1000m).Should().Be(expected);

    [Fact]
    public void Invalid_input_is_rejected()
    {
        var act1 = () => PricingEngine.Calculate([L("x", 10, 0)], OrderDiscount.None, DineIn);
        act1.Should().Throw<ArgumentException>();
        var act2 = () => PricingEngine.Calculate([L("x", 10, 1)], new OrderDiscount(DiscountType.Percent, 150), DineIn);
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Empty_order_totals_zero()
    {
        var r = PricingEngine.Calculate([], OrderDiscount.None, DineIn);
        r.Total.Should().Be(0);
    }

    [Fact]
    public void Allocate_puts_rounding_remainder_on_largest_weight()
    {
        var shares = PricingEngine.Allocate(10m, [1m, 1m, 1m]);
        shares.Sum().Should().Be(10m);
        shares.Should().Equal(3.34m, 3.33m, 3.33m);
    }
}

public class PaymentRulesTests
{
    private static readonly string[] Providers = ["bKash", "Nagad"];

    [Fact]
    public void Cash_payment_returns_change()
    {
        var r = PaymentRules.Validate(new PaymentRequest(PaymentMethod.Cash, 1167, 1200), 1167, Providers, null);
        r.Valid.Should().BeTrue();
        r.Change.Should().Be(33);
    }

    [Theory]
    [InlineData(PaymentMethod.Cash, 100, 50.0, "less than")]
    [InlineData(PaymentMethod.Card, 2000, null, "more than the due")]
    [InlineData(PaymentMethod.Cash, 0, null, "greater than zero")]
    [InlineData(PaymentMethod.Cash, 10.555, null, "2 decimal")]
    [InlineData("BITCOIN", 10, null, "method")]
    public void Invalid_payments_are_rejected(string method, decimal amount, double? tendered, string expected)
    {
        var r = PaymentRules.Validate(new PaymentRequest(method, amount, tendered is null ? null : (decimal)tendered), 1000, Providers, null);
        r.Valid.Should().BeFalse();
        r.Error.Should().Contain(expected);
    }

    [Fact]
    public void Mobile_banking_needs_provider_and_transaction_id()
    {
        PaymentRules.Validate(new PaymentRequest(PaymentMethod.MobileBanking, 500), 500, Providers, null).Valid.Should().BeFalse();
        PaymentRules.Validate(new PaymentRequest(PaymentMethod.MobileBanking, 500, Provider: "bKash"), 500, Providers, null).Error.Should().Contain("TrxID");
        PaymentRules.Validate(new PaymentRequest(PaymentMethod.MobileBanking, 500, Provider: "bkash", Reference: "8N7A6B5C4D"), 500, Providers, null).Valid.Should().BeTrue();
    }

    [Fact]
    public void Credit_needs_a_customer_within_limit()
    {
        var c = new Customer { Name = "Karim", CreditLimit = 1000, CreditBalance = 700 };
        PaymentRules.Validate(new PaymentRequest(PaymentMethod.Credit, 200), 500, Providers, null).Valid.Should().BeFalse();
        PaymentRules.Validate(new PaymentRequest(PaymentMethod.Credit, 400), 500, Providers, c).Error.Should().Contain("available credit");
        PaymentRules.Validate(new PaymentRequest(PaymentMethod.Credit, 300), 500, Providers, c).Valid.Should().BeTrue();
    }

    [Fact]
    public void Nothing_due_cannot_be_paid() =>
        PaymentRules.Validate(new PaymentRequest(PaymentMethod.Cash, 10), 0, Providers, null).Valid.Should().BeFalse();
}

public class CouponRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static Coupon C() => new() { Code = "WELCOME10", Type = DiscountType.Percent, Value = 10, MinOrder = 500, MaxDiscount = 200, IsActive = true };

    [Fact]
    public void Valid_coupon_is_capped() => CouponRules.Evaluate(C(), 3000, Now, false, 0, 0).DiscountAmount.Should().Be(200);

    [Fact]
    public void Coupon_conditions_are_enforced()
    {
        CouponRules.Evaluate(C(), 400, Now, false, 0, 0).Reason.Should().Contain("Minimum");
        CouponRules.Evaluate(C(), 1000, Now, false, 0, 0).Valid.Should().BeTrue();
        var expired = C(); expired.EndsAt = Now.AddDays(-1);
        CouponRules.Evaluate(expired, 1000, Now, false, 0, 0).Reason.Should().Contain("expired");
        var limited = C(); limited.UsageLimit = 10; limited.UsedCount = 9;
        CouponRules.Evaluate(limited, 1000, Now, false, 0, localUsageCount: 1).Valid.Should().BeFalse("unsynced local uses count too");
        var perCustomer = C(); perCustomer.PerCustomerLimit = 1;
        CouponRules.Evaluate(perCustomer, 1000, Now, hasCustomer: false, 0, 0).Valid.Should().BeFalse();
        CouponRules.Evaluate(perCustomer, 1000, Now, hasCustomer: true, customerUsageCount: 1, 0).Valid.Should().BeFalse();
        CouponRules.Evaluate(null, 1000, Now, false, 0, 0).Valid.Should().BeFalse();
    }
}

public class EscPosTests
{
    [Fact]
    public void Two_column_rows_fit_the_paper_and_unicode_is_sanitised()
    {
        var row = EscPos.Columns2("Kacchi Biryani (Full) x2 @890.00", "1,780.00", 32);
        row.Split('\n').Should().OnlyContain(l => l.Length <= 32);
        row.Should().EndWith("1,780.00");
        EscPos.Sanitize("৳ কাচ্চি").Should().StartWith("Tk").And.NotContain("ক").And.MatchRegex("^[ -~]*$");
        EscPos.Sanitize("৳500").Should().StartWith("Tk ");
    }

    [Fact]
    public void Render_starts_with_init_and_ends_with_cut()
    {
        var bytes = EscPos.Render([new ReceiptLine("Hello")], 48);
        bytes.Take(2).Should().Equal(0x1B, 0x40);
        bytes.TakeLast(4).Should().Equal(0x1D, 0x56, 0x42, 0x00);
    }
}
