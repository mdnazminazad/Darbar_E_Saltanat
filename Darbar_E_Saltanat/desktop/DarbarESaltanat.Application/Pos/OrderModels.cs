using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;

namespace DarbarESaltanat.Application.Pos;

public sealed record CreateOrderRequest(
    string OrderType, string? TableUuid = null, string? CustomerUuid = null, int GuestCount = 1,
    string? Notes = null, string? DeliveryAddress = null, string? DeliveryPhone = null);

public sealed record AddonSelection(string AddonUuid, int Quantity);

public sealed record AddItemRequest(string MenuItemUuid, string? VariantUuid, int Quantity, IReadOnlyList<AddonSelection>? Addons = null, string? Notes = null);

public sealed record SplitSelection(string OrderItemUuid, int Quantity);

public sealed record OrderLineView(
    string ItemUuid, string MenuItemUuid, string Name, string? AddonsText, int Quantity, decimal UnitPrice, decimal AddonsPerUnit,
    decimal LineTotal, decimal TaxRate, string Status, string? Notes, string? CancelReason)
{
    public bool IsNew => Status == OrderItemStatus.New;
    public bool IsCancelled => Status == OrderItemStatus.Cancelled;
}

/// <summary>Read model of one order for the POS screen, receipts and reports.</summary>
public sealed record OrderView(
    Order Order,
    IReadOnlyList<OrderLineView> Lines,
    IReadOnlyList<Payment> Payments,
    IReadOnlyList<Refund> Refunds,
    string? TableName,
    IReadOnlyList<string> MergedTableNames,
    Customer? Customer,
    string? WaiterName,
    string? CouponCode)
{
    public decimal TotalPayments => Payments.Where(p => p.Status == "COMPLETED").Sum(p => p.Amount);
    public decimal TotalRefunds => Refunds.Sum(r => r.Amount);
    public decimal NetPaid => TotalPayments - TotalRefunds;
    public bool HasNewItems => Lines.Any(l => l.IsNew);
    public bool IsClosed => OrderStatus.IsClosed(Order.Status);
    public string TablesLabel => TableName is null ? "" : MergedTableNames.Count == 0 ? TableName : $"{TableName} + {string.Join(", ", MergedTableNames)}";
}

/// <summary>Result of a POS action: the updated order plus anything to print and whether to pop the cash drawer.</summary>
public sealed record PosOutcome(OrderView View, IReadOnlyList<PrintJob> PrintJobs, bool OpenDrawer = false, decimal Change = 0, string? Notice = null);

public sealed record OrderSummary(
    string Uuid, string OrderNo, string OrderType, string Status, string PaymentStatus, string? TableName,
    string? CustomerName, decimal Total, decimal Due, DateTime OrderDateUtc, int ItemCount);
