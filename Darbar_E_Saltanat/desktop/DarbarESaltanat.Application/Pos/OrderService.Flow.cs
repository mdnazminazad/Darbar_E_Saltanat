using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Domain.Security;

namespace DarbarESaltanat.Application.Pos;

public sealed partial class OrderService
{
    // ------------------------------------------------------------------ hold / tables

    public async Task<Result<PosOutcome>> HoldAsync(string orderUuid, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersCreate);
        return await MutateAsync(orderUuid, ct, (uow, ctx, s) =>
        {
            if (ctx.Order.Status == OrderStatus.Held) return Task.FromResult<string?>("Order is already on hold");
            ctx.Order.Status = OrderStatus.Held;
            return Task.FromResult<string?>(null);
        });
    }

    public async Task<Result<PosOutcome>> ResumeAsync(string orderUuid, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersCreate);
        return await MutateAsync(orderUuid, ct, (uow, ctx, s) =>
        {
            if (ctx.Order.Status != OrderStatus.Held) return Task.FromResult<string?>(null);
            ctx.Order.Status = WorkingStatus(ctx);
            return Task.FromResult<string?>(null);
        });
    }

    public async Task<Result<PosOutcome>> TransferTableAsync(string orderUuid, string newTableUuid, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersTransfer);
        return await MutateAsync(orderUuid, ct, async (uow, ctx, s) =>
        {
            var o = ctx.Order;
            if (o.OrderType != OrderType.DineIn) return "Only dine-in orders have a table";
            if (o.TableUuid == newTableUuid) return "The order is already on this table";
            var target = await uow.FindAsync<RestaurantTable>(newTableUuid, ct);
            if (target is null || !target.IsActive) return "Table not found";
            if ((await ActiveOrdersOnTableAsync(uow, target.Uuid, ct)).Count > 0) return $"Table {target.Name} already has an open order — use Merge instead";
            if (target.Status == TableStatus.Cleaning) return $"Table {target.Name} is being cleaned";
            var oldName = o.TableUuid is null ? null : (await uow.FindAsync<RestaurantTable>(o.TableUuid, ct))?.Name;
            await ReleaseTablesAsync(uow, o, TableStatus.Available, ct);
            foreach (var link in await uow.ListAsync<OrderTable>(l => l.OrderUuid == o.Uuid, ct)) uow.Delete(link);
            o.TableUuid = target.Uuid;
            target.Status = o.Status == OrderStatus.Billed ? TableStatus.Billing : TableStatus.Occupied;
            uow.Add(audit.Create("orders.transfer", "orders", o.Uuid, new { table = oldName }, new { table = target.Name, o.OrderNo }));
            return null;
        });
    }

    /// <summary>Moves every item of <paramref name="sourceUuid"/> into <paramref name="targetUuid"/>; the source tables join the target order.</summary>
    public async Task<Result<PosOutcome>> MergeAsync(string targetUuid, string sourceUuid, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersTransfer);
        if (targetUuid == sourceUuid) return Fail("Choose a different order to merge");
        return await MutateAsync(targetUuid, ct, async (uow, ctx, s) =>
        {
            var src = await LoadAsync(uow, sourceUuid, ct);
            if (src is null || !OrderStatus.IsActive(src.Order.Status)) return "The other order is not open";
            if (src.Payments.Count > 0) return $"Order {src.Order.OrderNo} already has payments and cannot be merged";
            foreach (var item in src.Items) { item.OrderUuid = ctx.Order.Uuid; ctx.Items.Add(item); }
            ctx.Addons.AddRange(src.Addons);
            src.Items.Clear(); src.Addons.Clear();
            var tableIds = new List<string>();
            if (src.Order.TableUuid is not null) tableIds.Add(src.Order.TableUuid);
            foreach (var link in await uow.ListAsync<OrderTable>(l => l.OrderUuid == src.Order.Uuid, ct)) { tableIds.Add(link.TableUuid); uow.Delete(link); }
            var existing = (await uow.ListAsync<OrderTable>(l => l.OrderUuid == ctx.Order.Uuid, ct)).Select(l => l.TableUuid).ToHashSet();
            foreach (var t in tableIds.Distinct().Where(t => t != ctx.Order.TableUuid && !existing.Contains(t)))
                uow.Add(new OrderTable { OrderUuid = ctx.Order.Uuid, TableUuid = t });
            if (ctx.Order.TableUuid is null && tableIds.Count > 0) ctx.Order.TableUuid = tableIds[0];
            if (ctx.Order.CustomerUuid is null) ctx.Order.CustomerUuid = src.Order.CustomerUuid;
            ctx.Order.GuestCount = Math.Min(100, ctx.Order.GuestCount + src.Order.GuestCount);

            src.Order.Status = OrderStatus.Cancelled;
            src.Order.VoidReason = $"Merged into {ctx.Order.OrderNo}";
            Recalculate(src, s, null);
            ctx.Order.Status = ctx.Order.Status == OrderStatus.Held ? OrderStatus.Held : WorkingStatus(ctx);
            if (ctx.Order.OrderType == OrderType.DineIn) await SetTablesStatusAsync(uow, ctx.Order, TableStatus.Occupied, ct);
            uow.Add(audit.Create("orders.merge", "orders", ctx.Order.Uuid, null, new { into = ctx.Order.OrderNo, from = src.Order.OrderNo }));
            return null;
        });
    }

    /// <summary>Moves the selected quantities into a new order (separate bill) on the same table.</summary>
    public async Task<Result<PosOutcome>> SplitAsync(string orderUuid, IReadOnlyList<SplitSelection> selections, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersTransfer);
        var picks = selections.Where(x => x.Quantity > 0).ToList();
        if (picks.Count == 0) return Fail("Select the items to move to the new bill");
        var s = await settings.GetAsync(ct: ct);

        await using var uow = uowFactory.Begin();
        var ctx = await LoadAsync(uow, orderUuid, ct);
        if (ctx is null) return NotFound();
        var o = ctx.Order;
        if (!OrderStatus.IsActive(o.Status)) return Fail("Only open orders can be split");
        if (ctx.Payments.Count > 0) return Fail("This order already has payments; split before taking payment");
        var remaining = ctx.ActiveItems.Sum(i => i.Quantity) - picks.Sum(p => p.Quantity);
        if (remaining <= 0) return Fail("Leave at least one item on the original bill");

        var child = new Order
        {
            OrderNo = await NextOrderNoAsync(uow, s, ct), OrderType = o.OrderType, Status = OrderStatus.Open,
            TableUuid = o.TableUuid, CustomerUuid = null, WaiterUuid = o.WaiterUuid, GuestCount = 1,
            ServiceChargeRate = o.ServiceChargeRate, ParentOrderUuid = o.Uuid, OrderDate = clock.UtcNow,
            Notes = $"Split from {o.OrderNo}",
        };
        uow.Add(child);
        var childCtx = new OrderContext { Order = child, Items = [], Addons = [], Payments = [], Refunds = [] };
        foreach (var pick in picks)
        {
            var item = ctx.Items.FirstOrDefault(i => i.Uuid == pick.OrderItemUuid && !i.IsCancelled);
            if (item is null) return Fail("An item to split is no longer on the order");
            if (pick.Quantity > item.Quantity) return Fail($"{item.DisplayName}: only {item.Quantity} on the order");
            if (pick.Quantity == item.Quantity)
            {
                item.OrderUuid = child.Uuid;
                ctx.Items.Remove(item); childCtx.Items.Add(item);
                var adds = ctx.Addons.Where(a => a.OrderItemUuid == item.Uuid).ToList();
                childCtx.Addons.AddRange(adds);
                continue;
            }
            item.Quantity -= pick.Quantity;
            var copy = new OrderItem
            {
                OrderUuid = child.Uuid, MenuItemUuid = item.MenuItemUuid, VariantUuid = item.VariantUuid, ItemName = item.ItemName,
                VariantName = item.VariantName, Quantity = pick.Quantity, UnitPrice = item.UnitPrice, AddonsTotal = item.AddonsTotal,
                TaxRate = item.TaxRate, Status = item.Status, Notes = item.Notes, CreatedAt = clock.UtcNow,
            };
            uow.Add(copy); childCtx.Items.Add(copy);
            foreach (var a in ctx.Addons.Where(a => a.OrderItemUuid == item.Uuid).ToList())
            {
                var ac = new OrderItemAddon { OrderItemUuid = copy.Uuid, AddonUuid = a.AddonUuid, AddonName = a.AddonName, Quantity = a.Quantity, UnitPrice = a.UnitPrice };
                uow.Add(ac); childCtx.Addons.Add(ac);
            }
        }
        var coupon = o.CouponUuid is null ? null : await uow.FindAsync<Coupon>(o.CouponUuid, ct);
        var notice = Recalculate(ctx, s, coupon);
        Recalculate(childCtx, s, null);
        child.Status = WorkingStatus(childCtx);
        o.Status = o.Status == OrderStatus.Held ? o.Status : WorkingStatus(ctx);
        uow.Add(audit.Create("orders.split", "orders", o.Uuid, null, new { from = o.OrderNo, to = child.OrderNo, items = picks }));
        await uow.CommitAsync(ct);
        return Result<PosOutcome>.Ok(new PosOutcome(await BuildViewAsync(uow, childCtx, ct), [],
            Notice: (notice is null ? "" : notice + ". ") + $"New bill {child.OrderNo} created; {o.OrderNo} keeps the remaining items."));
    }

    // ------------------------------------------------------------------ bill, payment, completion

    /// <summary>Prints the bill (pre-payment). Dine-in tables move to BILLING.</summary>
    public async Task<Result<PosOutcome>> BillAsync(string orderUuid, CancellationToken ct = default)
    {
        var user = session.Require(Permissions.OrdersCreate);
        var s = await settings.GetAsync(ct: ct);
        var result = await MutateAsync(orderUuid, ct, async (uow, ctx, _) =>
        {
            if (!ctx.ActiveItems.Any()) return "The order has no items";
            ctx.Order.Status = OrderStatus.Billed;
            if (ctx.Order.OrderType == OrderType.DineIn) await SetTablesStatusAsync(uow, ctx.Order, TableStatus.Billing, ct);
            return null;
        });
        if (!result.Succeeded) return result;
        var v = result.Value!.View;
        var job = ReceiptFormatter.ForCustomer(v, s, ReceiptKind.Bill, user.FullName, clock.UtcNow);
        return Result<PosOutcome>.Ok(result.Value! with
        {
            PrintJobs = [job],
            Notice = v.HasNewItems ? "Note: some items have not been sent to the kitchen yet." : result.Value!.Notice,
        });
    }

    public async Task<Result<PosOutcome>> PayAsync(string orderUuid, PaymentRequest req, CancellationToken ct = default)
    {
        var user = session.Require(Permissions.PaymentsTake);
        var s = await settings.GetAsync(ct: ct);
        decimal change = 0;
        var completed = false;
        Payment? payment = null;
        var result = await MutateAsync(orderUuid, ct, async (uow, ctx, _) =>
        {
            var o = ctx.Order;
            if (o.Status == OrderStatus.Held) return "Resume the order before taking payment";
            if (!ctx.ActiveItems.Any()) return "The order has no items";

            if (o.DueAmount <= 0 && o.Total == 0)
            {   // fully discounted order: close without a payment row
                await CompleteAsync(uow, ctx, user.UserUuid, ct);
                completed = true;
                return null;
            }
            var customer = o.CustomerUuid is null ? null : await uow.FindAsync<Customer>(o.CustomerUuid, ct);
            var check = PaymentRules.Validate(req, o.DueAmount, s.MobileBankingProviders, customer);
            if (!check.Valid) return check.Error;

            payment = new Payment
            {
                OrderUuid = o.Uuid, ReceivedByUuid = user.UserUuid, Method = req.Method,
                Provider = Clean(req.Provider, 40), Reference = Clean(req.Reference, 100),
                Amount = req.Amount, Tendered = req.Method == PaymentMethod.Cash ? (req.Tendered ?? req.Amount) : null,
                ChangeAmount = check.Change, Status = "COMPLETED", PaidAt = clock.UtcNow,
                BranchUuid = o.BranchUuid ?? device.BranchUuid,
            };
            uow.Add(payment);
            ctx.Payments.Add(payment);
            if (req.Method == PaymentMethod.Credit) customer!.CreditBalance += req.Amount;
            change = check.Change;

            if (ctx.TotalPayments >= o.Total)
            {
                await CompleteAsync(uow, ctx, user.UserUuid, ct);
                completed = true;
            }
            return null;
        });
        if (!result.Succeeded) return result;
        var v = result.Value!.View;
        var jobs = completed ? new List<PrintJob> { ReceiptFormatter.ForCustomer(v, s, ReceiptKind.Receipt, user.FullName, clock.UtcNow, change) } : [];
        return Result<PosOutcome>.Ok(result.Value! with
        {
            PrintJobs = jobs, OpenDrawer = payment?.Method == PaymentMethod.Cash, Change = change,
            Notice = completed ? $"Order {v.Order.OrderNo} completed{(change > 0 ? $". Change: {Money.Format(change)}" : "")}" : $"Payment recorded. Still due: {Money.Format(v.Order.DueAmount)}",
        });
    }

    private async Task CompleteAsync(IUnitOfWork uow, OrderContext ctx, string cashierUuid, CancellationToken ct)
    {
        var o = ctx.Order;
        o.Status = OrderStatus.Completed;
        o.CompletedAt = clock.UtcNow;
        o.CashierUuid = cashierUuid;
        if (o.OrderType == OrderType.DineIn) await ReleaseTablesAsync(uow, o, TableStatus.Cleaning, ct);
        if (o.DiscountType == DiscountType.Coupon && o.CouponUuid is not null)
        {
            var already = await uow.CountAsync<CouponUsage>(u => u.OrderUuid == o.Uuid, ct: ct);
            if (already == 0)
                uow.Add(new CouponUsage { CouponUuid = o.CouponUuid, OrderUuid = o.Uuid, CustomerUuid = o.CustomerUuid, DiscountAmount = o.DiscountAmount, UsedAt = clock.UtcNow });
        }
        // NOTE: recipe-based stock deduction is added with Inventory (Phase 4), in this same transaction.
    }

    // ------------------------------------------------------------------ void, refund, reprint

    public async Task<Result<PosOutcome>> VoidAsync(string orderUuid, string reason, CancellationToken ct = default)
    {
        var user = session.Require(Permissions.OrdersVoid);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) return Fail("Enter a reason for voiding the order (at least 3 characters)");
        var s = await settings.GetAsync(ct: ct);
        var toCancel = new List<OrderItem>();
        var stations = new Dictionary<string, string>();
        var result = await MutateAsync(orderUuid, ct, async (uow, ctx, _) =>
        {
            var o = ctx.Order;
            if (ctx.TotalPayments - ctx.TotalRefunds > 0) return $"{Money.Format(ctx.TotalPayments - ctx.TotalRefunds)} has been paid on this order. Refund it before voiding.";
            toCancel = ctx.ActiveItems.Where(i => i.Status != OrderItemStatus.New).ToList();
            stations = await StationsAsync(uow, toCancel, ct);
            foreach (var i in ctx.ActiveItems.ToList())
            {
                i.Status = OrderItemStatus.Cancelled;
                i.CancelReason ??= "Order voided";
            }
            foreach (var kot in await uow.ListAsync<Kot>(k => k.OrderUuid == o.Uuid, ct)) kot.Status = KotStatus.Cancelled;
            o.Status = OrderStatus.Voided;
            o.VoidReason = Clean(reason, 500);
            o.VoidedAt = clock.UtcNow;
            o.VoidedByUuid = user.UserUuid;
            await ReleaseTablesAsync(uow, o, TableStatus.Available, ct);
            uow.Add(audit.Create("orders.void", "orders", o.Uuid, null, new { o.OrderNo, reason, total = o.Total }));
            return null;
        });
        if (!result.Succeeded) return result;
        var v = result.Value!.View;
        var jobs = toCancel.GroupBy(i => stations[i.Uuid]).Select(g => ReceiptFormatter.Kot($"{v.Order.OrderNo}-VOID", g.Key, v,
            g.Select(i => new ReceiptFormatter.KotLine(i.DisplayName, i.Quantity, null, "ORDER VOIDED")).ToList(), s, clock.UtcNow, cancellation: true)).ToList();
        return Result<PosOutcome>.Ok(result.Value! with { PrintJobs = jobs, Notice = $"Order {v.Order.OrderNo} voided" });
    }

    public async Task<Result<PosOutcome>> RefundAsync(string orderUuid, decimal amount, string method, string reason, bool restock = false, CancellationToken ct = default)
    {
        var user = session.Require(Permissions.PaymentsRefund);
        if (amount <= 0 || !Money.HasAtMostTwoDecimals(amount)) return Fail("Enter a valid refund amount");
        if (!PaymentMethod.All.Contains(method)) return Fail("Choose how the money is returned");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) return Fail("Enter a reason for the refund");
        var s = await settings.GetAsync(ct: ct);
        Refund? refund = null;
        var result = await MutateAsync(orderUuid, ct, async (uow, ctx, _) =>
        {
            var net = ctx.TotalPayments - ctx.TotalRefunds;
            if (net <= 0) return "Nothing has been paid on this order";
            if (amount > net) return $"Refund cannot exceed the amount paid ({Money.Format(net)})";
            if (method == PaymentMethod.Credit)
            {
                var customer = ctx.Order.CustomerUuid is null ? null : await uow.FindAsync<Customer>(ctx.Order.CustomerUuid, ct);
                if (customer is null) return "A credit refund needs the order's customer";
                customer.CreditBalance = Math.Max(0, customer.CreditBalance - amount);
            }
            refund = new Refund
            {
                OrderUuid = ctx.Order.Uuid, ApprovedByUuid = user.UserUuid, Amount = amount, Method = method,
                Reason = Clean(reason, 500)!, Restock = restock, RefundedAt = clock.UtcNow,
                PaymentUuid = ctx.Payments.LastOrDefault(p => p.Method == method)?.Uuid,
            };
            uow.Add(refund);
            ctx.Refunds.Add(refund);
            uow.Add(audit.Create("orders.refund", "orders", ctx.Order.Uuid, null, new { ctx.Order.OrderNo, amount, method, reason }));
            return null;
        }, allowClosed: true);
        if (!result.Succeeded) return result;
        var v = result.Value!.View;
        var job = ReceiptFormatter.ForCustomer(v, s, ReceiptKind.Refund, user.FullName, clock.UtcNow, refund: refund);
        return Result<PosOutcome>.Ok(result.Value! with { PrintJobs = [job], OpenDrawer = method == PaymentMethod.Cash, Notice = $"Refunded {Money.Format(amount)}" });
    }

    /// <summary>Reprints the receipt (completed orders) or the bill (open orders). Audited.</summary>
    public async Task<Result<PosOutcome>> ReprintAsync(string orderUuid, CancellationToken ct = default)
    {
        var user = session.Require(Permissions.OrdersReprint);
        var s = await settings.GetAsync(ct: ct);
        await using var uow = uowFactory.Begin();
        var ctx = await LoadAsync(uow, orderUuid, ct);
        if (ctx is null) return NotFound();
        uow.Add(audit.Create("orders.reprint", "orders", ctx.Order.Uuid, null, new { ctx.Order.OrderNo }));
        await uow.CommitAsync(ct);
        var v = await BuildViewAsync(uow, ctx, ct);
        var kind = ctx.Order.Status == OrderStatus.Completed ? ReceiptKind.Reprint : ReceiptKind.Bill;
        return Result<PosOutcome>.Ok(new PosOutcome(v, [ReceiptFormatter.ForCustomer(v, s, kind, user.FullName, clock.UtcNow)]));
    }
}
