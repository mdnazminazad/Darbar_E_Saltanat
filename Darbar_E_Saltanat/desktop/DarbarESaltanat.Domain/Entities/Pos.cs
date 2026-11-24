using DarbarESaltanat.Domain.Common;
using DarbarESaltanat.Domain.Enums;

namespace DarbarESaltanat.Domain.Entities;

// Column names are the snake_case of each property (see AppDbContext). Money is decimal (stored as exact TEXT).

[SyncTable("categories")]
public class Category : SyncEntity
{
    public string? ParentUuid { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    /// <summary>Kitchen station that receives this category's KOTs (e.g. Grill, Tandoor, Bar).</summary>
    public string? KitchenStation { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

[SyncTable("menu_items")]
public class MenuItem : SyncEntity
{
    public string CategoryUuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameBn { get; set; }
    public string? Description { get; set; }
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string? ImageUrl { get; set; }
    public decimal CostPrice { get; set; }
    public decimal SellingPrice { get; set; }
    /// <summary>Item-specific VAT %. 0 = use the restaurant VAT rate from settings.</summary>
    public decimal TaxRate { get; set; }
    public int PrepTimeMinutes { get; set; } = 15;
    public int SortOrder { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

[SyncTable("item_variants")]
public class ItemVariant : SyncEntity
{
    public string MenuItemUuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public decimal Price { get; set; }
    public decimal CostPrice { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

[SyncTable("item_addons")]
public class ItemAddon : SyncEntity
{
    public string Name { get; set; } = "";
    public string? GroupName { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
}

[SyncTable("item_addon_mappings")]
public class ItemAddonMapping : SyncEntity
{
    public string MenuItemUuid { get; set; } = "";
    public string AddonUuid { get; set; } = "";
    public int MaxQty { get; set; } = 1;
}

[SyncTable("restaurant_tables")]
public class RestaurantTable : SyncEntity
{
    public string Name { get; set; } = "";
    public int Capacity { get; set; } = 4;
    public string? Section { get; set; }
    public string Status { get; set; } = TableStatus.Available;
    public bool IsActive { get; set; } = true;
}

[SyncTable("discounts")]
public class Discount : SyncEntity
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = DiscountType.Percent;
    public decimal Value { get; set; }
    public decimal MinOrder { get; set; }
    public decimal? MaxDiscount { get; set; }
    public bool RequiresPermission { get; set; }
    public bool IsActive { get; set; } = true;
}

[SyncTable("coupons")]
public class Coupon : SyncEntity
{
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public string Type { get; set; } = DiscountType.Percent;
    public decimal Value { get; set; }
    public decimal MinOrder { get; set; }
    public decimal? MaxDiscount { get; set; }
    public int? UsageLimit { get; set; }
    public int? PerCustomerLimit { get; set; }
    /// <summary>Maintained by the server from coupon_usages; the desktop never edits it.</summary>
    public int UsedCount { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public bool IsActive { get; set; } = true;
}

[SyncTable("customers")]
public class Customer : SyncEntity
{
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public int LoyaltyPoints { get; set; }
    public decimal CreditBalance { get; set; }
    public decimal CreditLimit { get; set; }
    public bool IsActive { get; set; } = true;
}

[SyncTable("orders")]
[FinancialRecord]
public class Order : SyncEntity
{
    public string OrderNo { get; set; } = "";
    public string OrderType { get; set; } = Enums.OrderType.Takeaway;
    public string Source { get; set; } = "POS";
    public string Status { get; set; } = OrderStatus.Open;
    public string PaymentStatus { get; set; } = Enums.PaymentStatus.Unpaid;
    public string? TableUuid { get; set; }
    public string? CustomerUuid { get; set; }
    public string? WaiterUuid { get; set; }
    public string? CashierUuid { get; set; }
    public string? CouponUuid { get; set; }
    public string? ParentOrderUuid { get; set; }
    public string? VoidedByUuid { get; set; }
    public int GuestCount { get; set; } = 1;
    public decimal Subtotal { get; set; }
    public string DiscountType { get; set; } = Enums.DiscountType.None;
    public decimal DiscountValue { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ServiceChargeRate { get; set; }
    public decimal ServiceChargeAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal DeliveryCharge { get; set; }
    public decimal RoundOff { get; set; }
    public decimal Total { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal DueAmount { get; set; }
    public string? Notes { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? DeliveryPhone { get; set; }
    public string? VoidReason { get; set; }
    public DateTime? VoidedAt { get; set; }
    public DateTime OrderDate { get; set; }
    public DateTime? CompletedAt { get; set; }
}

[SyncTable("order_items")]
[FinancialRecord]
public class OrderItem : SyncEntity
{
    public string OrderUuid { get; set; } = "";
    public string MenuItemUuid { get; set; } = "";
    public string? VariantUuid { get; set; }
    public string ItemName { get; set; } = "";
    public string? VariantName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Add-on price PER UNIT of the item (Σ addon.unit_price × addon.quantity).</summary>
    public decimal AddonsTotal { get; set; }
    /// <summary>Item-level discount for the whole line.</summary>
    public decimal DiscountAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    /// <summary>(unit_price + addons_total) × quantity − discount_amount (before order-level discount/charges).</summary>
    public decimal LineTotal { get; set; }
    public string Status { get; set; } = OrderItemStatus.New;
    public string? Notes { get; set; }
    public string? CancelReason { get; set; }

    public bool IsCancelled => Status == OrderItemStatus.Cancelled;
    public string DisplayName => VariantName is null ? ItemName : $"{ItemName} ({VariantName})";
}

[SyncTable("order_item_addons")]
[FinancialRecord]
public class OrderItemAddon : SyncEntity
{
    public string OrderItemUuid { get; set; } = "";
    public string AddonUuid { get; set; } = "";
    public string AddonName { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
}

[SyncTable("order_tables")]
public class OrderTable : SyncEntity
{
    public string OrderUuid { get; set; } = "";
    public string TableUuid { get; set; } = "";
}

[SyncTable("kots")]
public class Kot : SyncEntity
{
    public string OrderUuid { get; set; } = "";
    public string KotNo { get; set; } = "";
    public string? Station { get; set; }
    public string Status { get; set; } = KotStatus.New;
    public string? Notes { get; set; }
    public DateTime? PrintedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? ReadyAt { get; set; }
}

[SyncTable("kot_items")]
public class KotItem : SyncEntity
{
    public string KotUuid { get; set; } = "";
    public string OrderItemUuid { get; set; } = "";
    public string ItemName { get; set; } = "";
    public int Quantity { get; set; }
    public string Status { get; set; } = KotStatus.New;
}

[SyncTable("payments")]
[AppendOnly]
[FinancialRecord]
public class Payment : SyncEntity
{
    public string OrderUuid { get; set; } = "";
    public string? ReceivedByUuid { get; set; }
    public string? CashSessionUuid { get; set; }
    public string Method { get; set; } = PaymentMethod.Cash;
    public string? Provider { get; set; }
    /// <summary>Amount applied to the order (excludes change returned).</summary>
    public decimal Amount { get; set; }
    public decimal? Tendered { get; set; }
    public decimal ChangeAmount { get; set; }
    public string? Reference { get; set; }
    public string Status { get; set; } = "COMPLETED";
    public DateTime PaidAt { get; set; }
}

[SyncTable("refunds")]
[AppendOnly]
[FinancialRecord]
public class Refund : SyncEntity
{
    public string OrderUuid { get; set; } = "";
    public string? PaymentUuid { get; set; }
    public string? ApprovedByUuid { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = PaymentMethod.Cash;
    public string Reason { get; set; } = "";
    public bool Restock { get; set; }
    public DateTime RefundedAt { get; set; }
}

[SyncTable("coupon_usages")]
[AppendOnly]
[FinancialRecord]
public class CouponUsage : SyncEntity
{
    public string CouponUuid { get; set; } = "";
    public string OrderUuid { get; set; } = "";
    public string? CustomerUuid { get; set; }
    public decimal DiscountAmount { get; set; }
    public DateTime UsedAt { get; set; }
}
