using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Domain.Security;
using FluentAssertions;
using Xunit;

namespace DarbarESaltanat.Tests;

public class OrderServiceTests
{
    private static async Task<string> ItemAsync(PosFixture f, string name) => (await f.FindAsync<MenuItem>(i => i.Name == name)).Uuid;
    private static async Task<string> TableAsync(PosFixture f, string name) => (await f.FindAsync<RestaurantTable>(t => t.Name == name)).Uuid;

    [Fact]
    public async Task Dine_in_order_full_flow_kot_bill_and_cash_payment()
    {
        await using var f = await PosFixture.CreateAsync();
        var t1 = await TableAsync(f, "T1");
        var order = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.DineIn, t1, GuestCount: 3))).Expect().View.Order;
        order.OrderNo.Should().Be("ORD-1111-261120-0001");
        (await f.FindAsync<RestaurantTable>(t => t.Uuid == t1)).Status.Should().Be(TableStatus.Occupied);

        var kacchi = await f.FindAsync<MenuItem>(i => i.Name == "Kacchi Biryani");
        var full = await f.FindAsync<ItemVariant>(v => v.MenuItemUuid == kacchi.Uuid && v.Name == "Full");
        var egg = await f.FindAsync<ItemAddon>(a => a.Name == "Extra Egg");
        (await f.Orders.AddItemAsync(order.Uuid, new AddItemRequest(kacchi.Uuid, full.Uuid, 1, [new AddonSelection(egg.Uuid, 2)], "less spicy"))).Expect();
        var view = (await f.Orders.AddItemAsync(order.Uuid, new AddItemRequest(await ItemAsync(f, "Borhani"), null, 2))).Expect().View;

        // 850 + 2×20 = 890; Borhani 2×60 = 120 → 1010; SC 10% = 101; VAT 5% of 1111 = 55.55; 1166.55 → ৳1167
        view.Order.Subtotal.Should().Be(1010m);
        view.Order.ServiceChargeAmount.Should().Be(101m);
        view.Order.VatAmount.Should().Be(55.55m);
        view.Order.Total.Should().Be(1167m);
        view.Order.RoundOff.Should().Be(0.45m);
        view.Lines.Should().HaveCount(2);
        view.Lines[0].AddonsText.Should().Be("Extra Egg x2");

        var kot = (await f.Orders.SendToKitchenAsync(order.Uuid)).Expect();
        kot.PrintJobs.Should().HaveCount(2, "Kacchi goes to Main Kitchen and Borhani to the Bar");
        kot.PrintJobs.Should().OnlyContain(j => j.Target == PrintTarget.Kitchen);
        kot.PrintJobs.Select(j => j.Station).Should().BeEquivalentTo(new[] { "Bar", "Main Kitchen" });
        kot.PrintJobs.Single(j => j.Station == "Main Kitchen").PreviewText.Should().Contain("less spicy").And.Contain("T1");
        kot.View.Order.Status.Should().Be(OrderStatus.InKitchen);
        kot.View.Lines.Should().OnlyContain(l => l.Status == OrderItemStatus.Sent);
        (await f.Orders.SendToKitchenAsync(order.Uuid)).Succeeded.Should().BeFalse("nothing new to send");

        var bill = (await f.Orders.BillAsync(order.Uuid)).Expect();
        bill.PrintJobs.Single().PreviewText.Should().Contain("BILL").And.Contain("1,167.00");
        (await f.FindAsync<RestaurantTable>(t => t.Uuid == t1)).Status.Should().Be(TableStatus.Billing);

        var paid = (await f.Orders.PayAsync(order.Uuid, new PaymentRequest(PaymentMethod.Cash, 1167m, 1200m))).Expect();
        paid.Change.Should().Be(33m);
        paid.OpenDrawer.Should().BeTrue();
        paid.View.Order.Status.Should().Be(OrderStatus.Completed);
        paid.View.Order.PaymentStatus.Should().Be(PaymentStatus.Paid);
        paid.View.Order.DueAmount.Should().Be(0);
        paid.PrintJobs.Single().PreviewText.Should().Contain("RECEIPT").And.Contain("Change");
        (await f.FindAsync<RestaurantTable>(t => t.Uuid == t1)).Status.Should().Be(TableStatus.Cleaning);

        // every change is queued for sync, in the same transactions
        var outbox = await f.Db.Db().SyncOutbox.ToListAsyncSafe();
        outbox.Select(o => o.EntityType).Distinct().Should().Contain(new[] { "orders", "order_items", "order_item_addons", "kots", "kot_items", "payments", "restaurant_tables" });
        outbox.Should().OnlyContain(o => o.Status == OutboxStatus.Pending);
        (await f.Orders.AddItemAsync(order.Uuid, new AddItemRequest(await ItemAsync(f, "Firni"), null, 1))).Succeeded.Should().BeFalse("completed orders are closed");
    }

    [Fact]
    public async Task Partial_payments_then_completion_and_mobile_banking()
    {
        await using var f = await PosFixture.CreateAsync();
        var o = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.Takeaway))).Expect().View.Order;
        var v = (await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(await ItemAsync(f, "Beef Tehari"), null, 2))).Expect().View;
        v.Order.ServiceChargeAmount.Should().Be(0, "service charge is dine-in only");
        v.Order.Total.Should().Be(672m); // 640 + 32 VAT

        var p1 = (await f.Orders.PayAsync(o.Uuid, new PaymentRequest(PaymentMethod.Cash, 300m))).Expect();
        p1.View.Order.PaymentStatus.Should().Be(PaymentStatus.Partial);
        p1.View.Order.DueAmount.Should().Be(372m);
        p1.PrintJobs.Should().BeEmpty();
        (await f.Orders.PayAsync(o.Uuid, new PaymentRequest(PaymentMethod.MobileBanking, 372m, Provider: "bKash"))).Error.Should().Contain("TrxID");
        var p2 = (await f.Orders.PayAsync(o.Uuid, new PaymentRequest(PaymentMethod.MobileBanking, 372m, Provider: "bKash", Reference: "BK8XY12345"))).Expect();
        p2.View.Order.Status.Should().Be(OrderStatus.Completed);
        p2.OpenDrawer.Should().BeFalse();
        p2.View.Payments.Should().HaveCount(2);
    }

    [Fact]
    public async Task Discount_limits_need_manager_override()
    {
        await using var f = await PosFixture.CreateAsync(PosFixture.CashierPermissions);
        var o = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.Takeaway))).Expect().View.Order;
        await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(await ItemAsync(f, "Mutton Kosha"), null, 2)); // 840
        var ok = (await f.Orders.ApplyDiscountAsync(o.Uuid, new DiscountRequest(DiscountType.Percent, 10))).Expect();
        ok.View.Order.DiscountAmount.Should().Be(84m);
        var tooMuch = () => f.Orders.ApplyDiscountAsync(o.Uuid, new DiscountRequest(DiscountType.Percent, 15));
        await tooMuch.Should().ThrowAsync<PermissionDeniedException>();
        var staff = await f.FindAsync<Discount>(d => d.Name == "Staff Meal 20%");
        var preset = () => f.Orders.ApplyDiscountAsync(o.Uuid, new DiscountRequest(staff.Type, staff.Value, PresetUuid: staff.Uuid));
        await preset.Should().ThrowAsync<PermissionDeniedException>("this preset requires permission");
        (await f.Orders.ApplyDiscountAsync(o.Uuid, new DiscountRequest(DiscountType.Fixed, 5000))).Error.Should().Contain("cannot exceed");
        (await f.Orders.ApplyDiscountAsync(o.Uuid, DiscountRequest.Clear())).Expect().View.Order.DiscountAmount.Should().Be(0);
    }

    [Fact]
    public async Task Coupon_is_validated_recorded_and_removed_when_order_drops_below_minimum()
    {
        await using var f = await PosFixture.CreateAsync();
        var o = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.Takeaway))).Expect().View.Order;
        var mutton = await ItemAsync(f, "Mutton Kosha"); // ৳420
        (await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(mutton, null, 3))).Expect().View.Order.Subtotal.Should().Be(1260m);
        (await f.Orders.ApplyDiscountAsync(o.Uuid, new DiscountRequest(DiscountType.Coupon, CouponCode: "flat100"))).Error.Should().Contain("Minimum");
        (await f.Orders.ApplyDiscountAsync(o.Uuid, new DiscountRequest(DiscountType.Coupon, CouponCode: "WELCOME10"))).Error.Should().Contain("customer", "WELCOME10 is limited per customer");

        var line = (await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(mutton, null, 1))).Expect().View.Lines.Single();
        line.Quantity.Should().Be(4, "identical new lines are merged");
        var withCoupon = (await f.Orders.ApplyDiscountAsync(o.Uuid, new DiscountRequest(DiscountType.Coupon, CouponCode: "FLAT100"))).Expect();
        withCoupon.View.Order.DiscountAmount.Should().Be(100m);
        withCoupon.View.CouponCode.Should().Be("FLAT100");

        var dropped = (await f.Orders.SetQuantityAsync(o.Uuid, line.ItemUuid, 3)).Expect();
        dropped.Notice.Should().Contain("removed");
        dropped.View.Order.DiscountAmount.Should().Be(0);

        (await f.Orders.SetQuantityAsync(o.Uuid, line.ItemUuid, 4)).Expect();
        (await f.Orders.ApplyDiscountAsync(o.Uuid, new DiscountRequest(DiscountType.Coupon, CouponCode: "FLAT100"))).Expect();
        var total = (await f.Orders.GetAsync(o.Uuid))!.Order.Total;
        total.Should().Be(1659m); // 1680 − 100 = 1580 + 5% VAT 79 = 1659
        var paid = (await f.Orders.PayAsync(o.Uuid, new PaymentRequest(PaymentMethod.Card, total, Reference: "4417"))).Expect();
        paid.PrintJobs.Single().PreviewText.Should().Contain("Coupon FLAT100");
        var usage = (await f.AllAsync<CouponUsage>()).Single();
        usage.OrderUuid.Should().Be(o.Uuid);
        usage.DiscountAmount.Should().Be(100m);
    }

    [Fact]
    public async Task Table_can_hold_only_one_open_order_and_transfer_frees_the_old_table()
    {
        await using var f = await PosFixture.CreateAsync();
        var t2 = await TableAsync(f, "T2"); var t3 = await TableAsync(f, "T3");
        var o = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.DineIn, t2))).Expect().View.Order;
        (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.DineIn, t2))).ErrorCode.Should().Be("TABLE_BUSY");
        (await f.Tables.SetStatusAsync(t2, TableStatus.Available)).Succeeded.Should().BeFalse("it has an open order");
        var moved = (await f.Orders.TransferTableAsync(o.Uuid, t3)).Expect();
        moved.View.TableName.Should().Be("T3");
        (await f.FindAsync<RestaurantTable>(t => t.Uuid == t2)).Status.Should().Be(TableStatus.Available);
        (await f.FindAsync<RestaurantTable>(t => t.Uuid == t3)).Status.Should().Be(TableStatus.Occupied);
        var board = await f.Tables.GetBoardAsync();
        board.Single(b => b.Table.Name == "T3").Primary!.OrderNo.Should().Be(o.OrderNo);
    }

    [Fact]
    public async Task Split_moves_selected_quantities_to_a_new_bill()
    {
        await using var f = await PosFixture.CreateAsync();
        var o = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.Takeaway))).Expect().View.Order;
        var kacchiLine = (await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(await ItemAsync(f, "Kacchi Biryani"), null, 2))).Expect().View.Lines.Single();
        await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(await ItemAsync(f, "Borhani"), null, 1));
        var child = (await f.Orders.SplitAsync(o.Uuid, [new SplitSelection(kacchiLine.ItemUuid, 1)])).Expect().View;
        child.Order.ParentOrderUuid.Should().Be(o.Uuid);
        child.Order.Subtotal.Should().Be(450m);
        child.Order.OrderNo.Should().EndWith("0002");
        var parent = (await f.Orders.GetAsync(o.Uuid))!;
        parent.Order.Subtotal.Should().Be(510m);
        parent.Lines.Single(l => l.Name.StartsWith("Kacchi")).Quantity.Should().Be(1);
        (await f.Orders.SplitAsync(o.Uuid, [new SplitSelection(kacchiLine.ItemUuid, 1), new SplitSelection(parent.Lines.Single(l => l.Name == "Borhani").ItemUuid, 1)]))
            .Error.Should().Contain("at least one item");
    }

    [Fact]
    public async Task Merge_moves_items_and_cancels_the_source()
    {
        await using var f = await PosFixture.CreateAsync();
        var a = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.DineIn, await TableAsync(f, "T4")))).Expect().View.Order;
        var b = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.DineIn, await TableAsync(f, "T5")))).Expect().View.Order;
        await f.Orders.AddItemAsync(a.Uuid, new AddItemRequest(await ItemAsync(f, "Firni"), null, 1));
        await f.Orders.AddItemAsync(b.Uuid, new AddItemRequest(await ItemAsync(f, "Jorda"), null, 2));
        var merged = (await f.Orders.MergeAsync(a.Uuid, b.Uuid)).Expect().View;
        merged.Lines.Should().HaveCount(2);
        merged.Order.Subtotal.Should().Be(290m);
        merged.TablesLabel.Should().Be("T4 + T5");
        (await f.Orders.GetAsync(b.Uuid))!.Order.Status.Should().Be(OrderStatus.Cancelled);
        (await f.FindAsync<RestaurantTable>(t => t.Name == "T5")).Status.Should().Be(TableStatus.Occupied);

        await f.Orders.PayAsync(a.Uuid, new PaymentRequest(PaymentMethod.Cash, merged.Order.Total));
        (await f.FindAsync<RestaurantTable>(t => t.Name == "T5")).Status.Should().Be(TableStatus.Cleaning, "merged tables are released together");
    }

    [Fact]
    public async Task Cancelling_a_sent_item_needs_permission_and_a_reason_and_prints_a_cancel_ticket()
    {
        await using var cashier = await PosFixture.CreateAsync(PosFixture.CashierPermissions);
        var o = (await cashier.Orders.CreateAsync(new CreateOrderRequest(OrderType.Takeaway))).Expect().View.Order;
        var line = (await cashier.Orders.AddItemAsync(o.Uuid, new AddItemRequest(await ItemAsync(cashier, "Chicken Roast"), null, 1))).Expect().View.Lines.Single();
        (await cashier.Orders.SendToKitchenAsync(o.Uuid)).Expect();
        var act = () => cashier.Orders.CancelItemAsync(o.Uuid, line.ItemUuid, "customer changed mind");
        await act.Should().ThrowAsync<PermissionDeniedException>();

        cashier.Session.SignIn(cashier.Session.Current! with { Roles = new[] { "SUPER_ADMIN" } });
        (await cashier.Orders.CancelItemAsync(o.Uuid, line.ItemUuid, "")).Error.Should().Contain("reason");
        var done = (await cashier.Orders.CancelItemAsync(o.Uuid, line.ItemUuid, "customer changed mind")).Expect();
        done.PrintJobs.Single().PreviewText.Should().Contain("CANCEL");
        done.View.Order.Total.Should().Be(0);
        done.View.Lines.Single().IsCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Void_is_blocked_while_money_is_held_and_refund_releases_it()
    {
        await using var f = await PosFixture.CreateAsync();
        var t6 = await TableAsync(f, "T6");
        var o = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.DineIn, t6))).Expect().View.Order;
        await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(await ItemAsync(f, "Beef Rezala"), null, 1));
        await f.Orders.PayAsync(o.Uuid, new PaymentRequest(PaymentMethod.Cash, 100m));
        (await f.Orders.VoidAsync(o.Uuid, "guest left")).Error.Should().Contain("Refund");
        (await f.Orders.VoidAsync(o.Uuid, "x")).Error.Should().Contain("reason");
        (await f.Orders.RefundAsync(o.Uuid, 150m, PaymentMethod.Cash, "guest left")).Error.Should().Contain("cannot exceed");
        var refund = (await f.Orders.RefundAsync(o.Uuid, 100m, PaymentMethod.Cash, "guest left")).Expect();
        refund.PrintJobs.Single().PreviewText.Should().Contain("REFUND");
        refund.View.Order.PaymentStatus.Should().Be(PaymentStatus.Refunded);
        var voided = (await f.Orders.VoidAsync(o.Uuid, "guest left")).Expect();
        voided.View.Order.Status.Should().Be(OrderStatus.Voided);
        voided.View.Order.VoidedByUuid.Should().Be(PosFixture.AdminUuid);
        (await f.FindAsync<RestaurantTable>(t => t.Uuid == t6)).Status.Should().Be(TableStatus.Available);
        (await f.AllAsync<AuditLog>()).Select(a => a.Action).Should().Contain(new[] { "orders.refund", "orders.void" });
    }

    [Fact]
    public async Task Out_of_stock_items_and_held_orders_are_protected()
    {
        await using var f = await PosFixture.CreateAsync();
        var firni = await ItemAsync(f, "Firni");
        (await f.Menu.SetAvailabilityAsync(firni, false)).Succeeded.Should().BeTrue();
        var o = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.Takeaway))).Expect().View.Order;
        (await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(firni, null, 1))).Error.Should().Contain("out of stock");
        (await f.Orders.HoldAsync(o.Uuid)).Expect().View.Order.Status.Should().Be(OrderStatus.Held);
        (await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(await ItemAsync(f, "Jorda"), null, 1))).Error.Should().Contain("hold");
        (await f.Orders.ResumeAsync(o.Uuid)).Expect().View.Order.Status.Should().Be(OrderStatus.Open);
        (await f.Orders.ListActiveAsync()).Should().ContainSingle(s => s.Uuid == o.Uuid);
    }

    [Fact]
    public async Task Fully_discounted_order_closes_without_a_payment()
    {
        await using var f = await PosFixture.CreateAsync();
        var o = (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.Takeaway))).Expect().View.Order;
        await f.Orders.AddItemAsync(o.Uuid, new AddItemRequest(await ItemAsync(f, "Doodh Cha"), null, 1));
        (await f.Orders.ApplyDiscountAsync(o.Uuid, new DiscountRequest(DiscountType.Percent, 100))).Expect().View.Order.Total.Should().Be(0);
        var closed = (await f.Orders.PayAsync(o.Uuid, new PaymentRequest(PaymentMethod.Cash, 0))).Expect();
        closed.View.Order.Status.Should().Be(OrderStatus.Completed);
        closed.View.Payments.Should().BeEmpty();
    }
}

internal static class QueryHelpers
{
    public static async Task<List<SyncOutboxItem>> ToListAsyncSafe(this Microsoft.EntityFrameworkCore.DbSet<SyncOutboxItem> set) =>
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(set);
}
