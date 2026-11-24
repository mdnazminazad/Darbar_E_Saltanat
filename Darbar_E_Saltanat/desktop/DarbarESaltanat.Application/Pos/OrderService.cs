using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Domain.Security;

namespace DarbarESaltanat.Application.Pos;

/// <summary>
/// All POS order operations. Every operation: permission check → load the order into a short unit of work →
/// mutate → recalculate with <see cref="PricingEngine"/> → validate → ONE commit (rows + sync outbox) →
/// return the refreshed view and anything to print. Printing happens after the commit, so a printer
/// problem can never lose an order.
/// </summary>
public sealed partial class OrderService(
    IUnitOfWorkFactory uowFactory, ICurrentSession session, IClock clock, IDeviceContext device,
    SettingsService settings, AuditFactory audit)
{
    private sealed class OrderContext
    {
        public required Order Order { get; init; }
        public required List<OrderItem> Items { get; init; }
        public required List<OrderItemAddon> Addons { get; init; }
        public required List<Payment> Payments { get; init; }
        public required List<Refund> Refunds { get; init; }
        public IEnumerable<OrderItem> ActiveItems => Items.Where(i => !i.IsCancelled);
        public decimal TotalPayments => Payments.Where(p => p.Status == "COMPLETED").Sum(p => p.Amount);
        public decimal TotalRefunds => Refunds.Sum(r => r.Amount);
    }

    private static Result<PosOutcome> Fail(string message, string? code = null) => Result<PosOutcome>.Fail(message, code);
    private static Result<PosOutcome> NotFound() => Fail("Order not found", "NOT_FOUND");

    // ------------------------------------------------------------------ queries

    public async Task<OrderView?> GetAsync(string orderUuid, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersView);
        await using var uow = uowFactory.Begin();
        var ctx = await LoadAsync(uow, orderUuid, ct);
        return ctx is null ? null : await BuildViewAsync(uow, ctx, ct);
    }

    /// <summary>Orders still in progress on this terminal (open, held, in kitchen, billed…), newest first.</summary>
    public async Task<IReadOnlyList<OrderSummary>> ListActiveAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersView);
        var active = OrderStatus.Active.ToHashSet();
        await using var uow = uowFactory.Begin();
        var all = await uow.ListAsync<Order>(null, ct);
        var orders = all.Where(o => active.Contains(o.Status)).ToList();
        return await SummariesAsync(uow, orders, ct);
    }

    public async Task<IReadOnlyList<OrderSummary>> ListAsync(DateTime fromUtc, DateTime toUtc, string? status = null, string? search = null, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersView);
        await using var uow = uowFactory.Begin();
        var orders = await uow.ListAsync<Order>(o => o.OrderDate >= fromUtc && o.OrderDate < toUtc, ct);
        if (!string.IsNullOrWhiteSpace(status)) orders = orders.Where(o => o.Status == status).ToList();
        var list = await SummariesAsync(uow, orders, ct);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            list = list.Where(s => s.OrderNo.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || (s.CustomerName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                   || (s.TableName?.Equals(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }
        return list;
    }

    private static async Task<List<OrderSummary>> SummariesAsync(IUnitOfWork uow, List<Order> orders, CancellationToken ct)
    {
        var ids = orders.Select(o => o.Uuid).ToList();
        var tableIds = orders.Where(o => o.TableUuid != null).Select(o => o.TableUuid!).Distinct().ToList();
        var custIds = orders.Where(o => o.CustomerUuid != null).Select(o => o.CustomerUuid!).Distinct().ToList();
        var tables = tableIds.Count == 0 ? [] : await uow.ListAsync<RestaurantTable>(t => tableIds.Contains(t.Uuid), ct);
        var customers = custIds.Count == 0 ? [] : await uow.ListAsync<Customer>(c => custIds.Contains(c.Uuid), ct);
        var items = ids.Count == 0 ? [] : await uow.ListAsync<OrderItem>(i => ids.Contains(i.OrderUuid) && i.Status != OrderItemStatus.Cancelled, ct);
        return orders.OrderByDescending(o => o.OrderDate).Select(o => new OrderSummary(
            o.Uuid, o.OrderNo, o.OrderType, o.Status, o.PaymentStatus,
            tables.FirstOrDefault(t => t.Uuid == o.TableUuid)?.Name, customers.FirstOrDefault(c => c.Uuid == o.CustomerUuid)?.Name,
            o.Total, o.DueAmount, o.OrderDate, items.Where(i => i.OrderUuid == o.Uuid).Sum(i => i.Quantity))).ToList();
    }

    // ------------------------------------------------------------------ create & details

    public async Task<Result<PosOutcome>> CreateAsync(CreateOrderRequest req, CancellationToken ct = default)
    {
        var user = session.Require(Permissions.OrdersCreate);
        if (!OrderType.All.Contains(req.OrderType)) return Fail("Choose dine-in, takeaway or delivery");
        if (req.GuestCount is < 1 or > 100) return Fail("Guest count must be between 1 and 100");
        if (req.OrderType == OrderType.DineIn && string.IsNullOrEmpty(req.TableUuid)) return Fail("Select a table for a dine-in order");
        if (req.OrderType == OrderType.Delivery && string.IsNullOrWhiteSpace(req.DeliveryAddress)) return Fail("Enter the delivery address");
        var s = await settings.GetAsync(ct: ct);

        await using var uow = uowFactory.Begin();
        RestaurantTable? table = null;
        if (req.OrderType == OrderType.DineIn)
        {
            table = await uow.FindAsync<RestaurantTable>(req.TableUuid!, ct);
            if (table is null || !table.IsActive) return Fail("Table not found");
            if ((await ActiveOrdersOnTableAsync(uow, table.Uuid, ct)).Count > 0) return Fail($"Table {table.Name} already has an open order", "TABLE_BUSY");
            if (table.Status == TableStatus.Cleaning) return Fail($"Table {table.Name} is being cleaned. Mark it available first.");
            table.Status = TableStatus.Occupied;
        }
        if (req.CustomerUuid is not null && await uow.FindAsync<Customer>(req.CustomerUuid, ct) is null) return Fail("Customer not found");

        var applySc = s.ServiceChargeRate > 0 && (!s.ServiceChargeDineInOnly || req.OrderType == OrderType.DineIn);
        var order = new Order
        {
            OrderNo = await NextOrderNoAsync(uow, s, ct),
            OrderType = req.OrderType,
            Status = OrderStatus.Open,
            TableUuid = table?.Uuid,
            CustomerUuid = req.CustomerUuid,
            WaiterUuid = user.UserUuid,
            GuestCount = req.GuestCount,
            ServiceChargeRate = applySc ? s.ServiceChargeRate : 0,
            DeliveryCharge = req.OrderType == OrderType.Delivery ? s.DeliveryChargeDefault : 0,
            Notes = Clean(req.Notes, 500),
            DeliveryAddress = req.OrderType == OrderType.Delivery ? Clean(req.DeliveryAddress, 500) : null,
            DeliveryPhone = req.OrderType == OrderType.Delivery ? Clean(req.DeliveryPhone, 30) : null,
            OrderDate = clock.UtcNow,
            BranchUuid = device.BranchUuid,
        };
        uow.Add(order);
        var ctx = new OrderContext { Order = order, Items = [], Addons = [], Payments = [], Refunds = [] };
        Recalculate(ctx, s, null);
        await uow.CommitAsync(ct);
        return Result<PosOutcome>.Ok(new PosOutcome(await BuildViewAsync(uow, ctx, ct), []));
    }

    public async Task<Result<PosOutcome>> UpdateDetailsAsync(string orderUuid, string? notes, int guestCount, string? deliveryAddress, string? deliveryPhone, decimal? deliveryCharge, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersCreate);
        if (guestCount is < 1 or > 100) return Fail("Guest count must be between 1 and 100");
        if (deliveryCharge is < 0 or > 10000) return Fail("Delivery charge must be between 0 and 10,000");
        return await MutateAsync(orderUuid, ct, (uow, ctx, s) =>
        {
            var o = ctx.Order;
            o.Notes = Clean(notes, 500);
            o.GuestCount = guestCount;
            if (o.OrderType == OrderType.Delivery)
            {
                if (string.IsNullOrWhiteSpace(deliveryAddress)) return Task.FromResult<string?>("Enter the delivery address");
                o.DeliveryAddress = Clean(deliveryAddress, 500);
                o.DeliveryPhone = Clean(deliveryPhone, 30);
                if (deliveryCharge is { } dc) o.DeliveryCharge = Money.Round2(dc);
            }
            return Task.FromResult<string?>(null);
        });
    }

    public async Task<Result<PosOutcome>> SetCustomerAsync(string orderUuid, string? customerUuid, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersCreate);
        return await MutateAsync(orderUuid, ct, async (uow, ctx, s) =>
        {
            if (customerUuid is not null)
            {
                var c = await uow.FindAsync<Customer>(customerUuid, ct);
                if (c is null || !c.IsActive) return "Customer not found";
                if (ctx.Order.OrderType == OrderType.Delivery && string.IsNullOrWhiteSpace(ctx.Order.DeliveryPhone)) ctx.Order.DeliveryPhone = c.Phone;
            }
            if (customerUuid is null && ctx.Payments.Any(p => p.Method == PaymentMethod.Credit)) return "This order has a credit payment; the customer cannot be removed";
            ctx.Order.CustomerUuid = customerUuid;
            return null;
        });
    }

    // ------------------------------------------------------------------ shared plumbing

    /// <summary>Load → mutate → recalc → guard → commit. The mutator returns an error message to abort.</summary>
    private async Task<Result<PosOutcome>> MutateAsync(string orderUuid, CancellationToken ct,
        Func<IUnitOfWork, OrderContext, RestaurantSettings, Task<string?>> mutate, bool allowClosed = false)
    {
        var s = await settings.GetAsync(ct: ct);
        await using var uow = uowFactory.Begin();
        var ctx = await LoadAsync(uow, orderUuid, ct);
        if (ctx is null) return NotFound();
        if (!allowClosed && OrderStatus.IsClosed(ctx.Order.Status)) return Fail($"Order {ctx.Order.OrderNo} is {ctx.Order.Status.ToLowerInvariant()} and can no longer be changed");
        var error = await mutate(uow, ctx, s);
        if (error is not null) return Fail(error);
        var coupon = ctx.Order.CouponUuid is null ? null : await uow.FindAsync<Coupon>(ctx.Order.CouponUuid, ct);
        var notice = Recalculate(ctx, s, coupon);
        if (ctx.TotalPayments - ctx.TotalRefunds > ctx.Order.Total)
            return Fail($"The new total {Money.Format(ctx.Order.Total)} is less than what was already paid ({Money.Format(ctx.TotalPayments - ctx.TotalRefunds)}). Refund the difference first.");
        await uow.CommitAsync(ct);
        return Result<PosOutcome>.Ok(new PosOutcome(await BuildViewAsync(uow, ctx, ct), [], Notice: notice));
    }

    private static async Task<OrderContext?> LoadAsync(IUnitOfWork uow, string orderUuid, CancellationToken ct)
    {
        var order = await uow.FindAsync<Order>(orderUuid, ct);
        if (order is null) return null;
        var items = await uow.ListAsync<OrderItem>(i => i.OrderUuid == orderUuid, ct);
        var itemIds = items.Select(i => i.Uuid).ToList();
        var addons = itemIds.Count == 0 ? [] : await uow.ListAsync<OrderItemAddon>(a => itemIds.Contains(a.OrderItemUuid), ct);
        var payments = await uow.ListAsync<Payment>(p => p.OrderUuid == orderUuid, ct);
        var refunds = await uow.ListAsync<Refund>(r => r.OrderUuid == orderUuid, ct);
        return new OrderContext
        {
            Order = order,
            Items = items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToList(),
            Addons = addons, Payments = payments.OrderBy(p => p.PaidAt).ToList(), Refunds = refunds,
        };
    }

    /// <summary>Applies the pricing engine to the order and its lines; returns a notice when a coupon had to be removed.</summary>
    private static string? Recalculate(OrderContext ctx, RestaurantSettings s, Coupon? coupon)
    {
        var o = ctx.Order;
        string? notice = null;
        var active = ctx.ActiveItems.ToList();
        var lines = active.Select(i => new PricingLine(i.Uuid, i.UnitPrice, i.AddonsTotal, i.Quantity, i.DiscountAmount, i.TaxRate)).ToList();

        if (o.DiscountType == DiscountType.Coupon && coupon is not null)
        {
            var subtotal = lines.Sum(l => Money.Round2((l.UnitPrice + l.AddonsPerUnit) * l.Quantity - l.LineDiscount));
            if (subtotal < coupon.MinOrder)
            {
                notice = $"Coupon {coupon.Code} removed: order is below its minimum of {Money.Format(coupon.MinOrder)}";
                o.DiscountType = DiscountType.None; o.DiscountValue = 0; o.CouponUuid = null; coupon = null;
            }
        }
        var discount = o.DiscountType switch
        {
            DiscountType.Percent => new OrderDiscount(DiscountType.Percent, o.DiscountValue),
            DiscountType.Fixed => new OrderDiscount(DiscountType.Fixed, o.DiscountValue),
            DiscountType.Coupon when coupon is not null => CouponRules.ToOrderDiscount(coupon),
            _ => OrderDiscount.None,
        };
        var r = PricingEngine.Calculate(lines, discount,
            new PricingOptions(o.ServiceChargeRate, s.VatInclusive, s.VatAppliesToServiceCharge, o.DeliveryCharge, s.RoundTotalToTaka));

        o.Subtotal = r.Subtotal;
        o.DiscountAmount = r.DiscountAmount;
        o.ServiceChargeAmount = r.ServiceCharge;
        o.VatAmount = r.Vat;
        o.RoundOff = r.RoundOff;
        o.Total = r.Total;
        foreach (var pl in r.Lines)
        {
            var item = active.First(i => i.Uuid == pl.Key);
            item.LineTotal = pl.LineTotal;
            item.TaxAmount = pl.Vat;
        }
        foreach (var c in ctx.Items.Where(i => i.IsCancelled)) c.TaxAmount = 0;

        var paid = ctx.TotalPayments;
        var refunded = ctx.TotalRefunds;
        o.PaidAmount = paid - refunded;
        o.DueAmount = Math.Max(0, o.Total - paid);
        o.PaymentStatus = refunded > 0
            ? (refunded >= paid ? PaymentStatus.Refunded : PaymentStatus.PartialRefund)
            : paid <= 0 ? PaymentStatus.Unpaid
            : paid < o.Total ? PaymentStatus.Partial
            : PaymentStatus.Paid;
        return notice;
    }

    /// <summary>OPEN, or IN_KITCHEN when anything is with the kitchen.</summary>
    private static string WorkingStatus(OrderContext ctx) =>
        ctx.ActiveItems.Any(i => i.Status is OrderItemStatus.Sent or OrderItemStatus.Preparing) ? OrderStatus.InKitchen
        : ctx.ActiveItems.Any() && ctx.ActiveItems.All(i => i.Status is OrderItemStatus.Ready or OrderItemStatus.Served) ? OrderStatus.Served
        : OrderStatus.Open;

    private async Task<OrderView> BuildViewAsync(IUnitOfWork uow, OrderContext ctx, CancellationToken ct)
    {
        var o = ctx.Order;
        var table = o.TableUuid is null ? null : await uow.FindAsync<RestaurantTable>(o.TableUuid, ct);
        var links = await uow.ListAsync<OrderTable>(l => l.OrderUuid == o.Uuid, ct);
        var linkIds = links.Select(l => l.TableUuid).ToList();
        var merged = linkIds.Count == 0 ? [] : (await uow.ListAsync<RestaurantTable>(t => linkIds.Contains(t.Uuid), ct)).Select(t => t.Name).ToList();
        var customer = o.CustomerUuid is null ? null : await uow.FindAsync<Customer>(o.CustomerUuid, ct);
        var waiter = o.WaiterUuid is null ? null : await uow.FindAsync<User>(o.WaiterUuid, ct);
        var coupon = o.CouponUuid is null ? null : await uow.FindAsync<Coupon>(o.CouponUuid, ct);
        var lines = ctx.Items.Select(i =>
        {
            var adds = ctx.Addons.Where(a => a.OrderItemUuid == i.Uuid).ToList();
            var text = adds.Count == 0 ? null : string.Join(", ", adds.Select(a => a.Quantity > 1 ? $"{a.AddonName} x{a.Quantity}" : a.AddonName));
            return new OrderLineView(i.Uuid, i.MenuItemUuid, i.DisplayName, text, i.Quantity, i.UnitPrice, i.AddonsTotal,
                i.LineTotal, i.TaxRate, i.Status, i.Notes, i.CancelReason);
        }).ToList();
        return new OrderView(o, lines, ctx.Payments.ToList(), ctx.Refunds.ToList(), table?.Name, merged, customer, waiter?.FullName, coupon?.Code);
    }

    private async Task<string> NextOrderNoAsync(IUnitOfWork uow, RestaurantSettings s, CancellationToken ct)
    {
        var local = Dhaka.ToLocal(clock.UtcNow);
        var code = new string(device.DeviceId.Where(char.IsLetterOrDigit).Take(4).ToArray()).ToUpperInvariant();
        var prefix = $"{s.OrderNoPrefix}-{code}-{local:yyMMdd}-";
        var n = await uow.CountAsync<Order>(o => o.OrderNo.StartsWith(prefix), includeDeleted: true, ct) + 1;
        return prefix + n.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<Order>> ActiveOrdersOnTableAsync(IUnitOfWork uow, string tableUuid, CancellationToken ct)
    {
        var active = OrderStatus.Active.ToHashSet();
        var allForTable = await uow.ListAsync<Order>(o => o.TableUuid == tableUuid, ct);
        var direct = allForTable.Where(o => active.Contains(o.Status)).ToList();
        var linkIds = (await uow.ListAsync<OrderTable>(l => l.TableUuid == tableUuid, ct)).Select(l => l.OrderUuid).ToHashSet();
        var linked = linkIds.Count == 0 ? [] : (await uow.ListAsync<Order>(null, ct)).Where(o => linkIds.Contains(o.Uuid) && active.Contains(o.Status)).ToList();
        return direct.Concat(linked).Where(o => OrderStatus.IsActive(o.Status)).DistinctBy(o => o.Uuid).ToList();
    }

    /// <summary>Frees the order's tables (own + merged) unless another active order still uses them.</summary>
    private static async Task ReleaseTablesAsync(IUnitOfWork uow, Order order, string newStatus, CancellationToken ct)
    {
        var ids = new List<string>();
        if (order.TableUuid is not null) ids.Add(order.TableUuid);
        ids.AddRange((await uow.ListAsync<OrderTable>(l => l.OrderUuid == order.Uuid, ct)).Select(l => l.TableUuid));
        foreach (var id in ids.Distinct())
        {
            if ((await ActiveOrdersOnTableAsync(uow, id, ct)).Any(o => o.Uuid != order.Uuid)) continue;
            var t = await uow.FindAsync<RestaurantTable>(id, ct);
            if (t is not null) t.Status = newStatus;
        }
    }

    private static async Task SetTablesStatusAsync(IUnitOfWork uow, Order order, string status, CancellationToken ct)
    {
        if (order.TableUuid is null) return;
        var ids = new List<string> { order.TableUuid };
        ids.AddRange((await uow.ListAsync<OrderTable>(l => l.OrderUuid == order.Uuid, ct)).Select(l => l.TableUuid));
        foreach (var id in ids.Distinct())
            if (await uow.FindAsync<RestaurantTable>(id, ct) is { } t) t.Status = status;
    }

    private static string? Clean(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length > max ? s.Trim()[..max] : s.Trim());
}
