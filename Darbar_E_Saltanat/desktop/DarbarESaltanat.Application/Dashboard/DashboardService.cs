using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Domain.Security;

namespace DarbarESaltanat.Application.Dashboard;

public sealed record TopItem(string Name, int Quantity, decimal Amount);

public sealed record DashboardStats(
    DateTime FromUtc, DateTime ToUtc,
    decimal NetSales, int CompletedOrders, decimal AverageOrderValue,
    decimal Cash, decimal Card, decimal MobileBanking, decimal Credit, decimal Other, decimal Refunds,
    decimal Discounts, decimal Vat, decimal ServiceCharge,
    int OpenOrders, int ActiveTables, int PendingKitchenTickets, int VoidedOrders,
    IReadOnlyList<TopItem> TopItems);

/// <summary>KPIs computed from local SQLite (works offline). Money is summed in memory with decimal precision.</summary>
public sealed class DashboardService(IUnitOfWorkFactory uowFactory, ICurrentSession session, IClock clock)
{
    public enum Period { Today, Last7Days, Last30Days }

    public (DateTime From, DateTime To) Range(Period p)
    {
        var start = Dhaka.StartOfDayUtc(clock.UtcNow);
        var end = start.AddDays(1);
        return p switch { Period.Last7Days => (start.AddDays(-6), end), Period.Last30Days => (start.AddDays(-29), end), _ => (start, end) };
    }

    public async Task<DashboardStats> GetAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersView);
        var active = OrderStatus.Active;
        await using var uow = uowFactory.Begin();
        var orders = await uow.ListAsync<Order>(o => o.OrderDate >= fromUtc && o.OrderDate < toUtc, ct);
        var completed = orders.Where(o => o.Status == OrderStatus.Completed).ToList();
        var completedIds = completed.Select(o => o.Uuid).ToList();
        var payments = await uow.ListAsync<Payment>(p => p.PaidAt >= fromUtc && p.PaidAt < toUtc && p.Status == "COMPLETED", ct);
        var refunds = await uow.ListAsync<Refund>(r => r.RefundedAt >= fromUtc && r.RefundedAt < toUtc, ct);
        var items = completedIds.Count == 0 ? [] : await uow.ListAsync<OrderItem>(i => completedIds.Contains(i.OrderUuid) && i.Status != OrderItemStatus.Cancelled, ct);
        var openOrders = await uow.ListAsync<Order>(o => active.Contains(o.Status), ct);
        var pendingKots = await uow.CountAsync<Kot>(k => k.Status == KotStatus.New || k.Status == KotStatus.Preparing, ct: ct);
        var occupied = await uow.CountAsync<RestaurantTable>(t => t.IsActive && (t.Status == TableStatus.Occupied || t.Status == TableStatus.Billing), ct: ct);

        decimal Sum(string m) => payments.Where(p => p.Method == m).Sum(p => p.Amount);
        var refundTotal = refunds.Sum(r => r.Amount);
        var gross = completed.Sum(o => o.Total);
        var net = gross - refundTotal;
        var top = items.GroupBy(i => i.ItemName)
            .Select(g => new TopItem(g.Key, g.Sum(i => i.Quantity), g.Sum(i => i.LineTotal)))
            .OrderByDescending(t => t.Quantity).ThenByDescending(t => t.Amount).Take(8).ToList();

        return new DashboardStats(fromUtc, toUtc, net, completed.Count, completed.Count == 0 ? 0 : Money.Round2(gross / completed.Count),
            Sum(PaymentMethod.Cash), Sum(PaymentMethod.Card), Sum(PaymentMethod.MobileBanking), Sum(PaymentMethod.Credit), Sum(PaymentMethod.Other), refundTotal,
            completed.Sum(o => o.DiscountAmount), completed.Sum(o => o.VatAmount), completed.Sum(o => o.ServiceChargeAmount),
            openOrders.Count, occupied, pendingKots, orders.Count(o => o.Status == OrderStatus.Voided), top);
    }
}
