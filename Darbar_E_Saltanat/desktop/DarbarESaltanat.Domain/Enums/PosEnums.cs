namespace DarbarESaltanat.Domain.Enums;

// String constants match the CHECK/ENUM values in the SQLite and MySQL schemas exactly.

public static class OrderType
{
    public const string DineIn = "DINE_IN";
    public const string Takeaway = "TAKEAWAY";
    public const string Delivery = "DELIVERY";
    public static readonly string[] All = [DineIn, Takeaway, Delivery];
    public static string Label(string t) => t switch { DineIn => "Dine-in", Takeaway => "Takeaway", Delivery => "Delivery", _ => t };
}

public static class OrderStatus
{
    public const string Draft = "DRAFT";
    public const string Held = "HELD";
    public const string Open = "OPEN";
    public const string InKitchen = "IN_KITCHEN";
    public const string Ready = "READY";
    public const string Served = "SERVED";
    public const string Billed = "BILLED";
    public const string Completed = "COMPLETED";
    public const string Voided = "VOIDED";
    public const string Cancelled = "CANCELLED";

    /// <summary>Orders still being worked on (items can be added / payment taken).</summary>
    public static readonly string[] Active = [Draft, Held, Open, InKitchen, Ready, Served, Billed];
    public static bool IsActive(string s) => Active.Contains(s);
    public static bool IsClosed(string s) => s is Completed or Voided or Cancelled;
}

public static class PaymentStatus
{
    public const string Unpaid = "UNPAID";
    public const string Partial = "PARTIAL";
    public const string Paid = "PAID";
    public const string Refunded = "REFUNDED";
    public const string PartialRefund = "PARTIAL_REFUND";
}

public static class OrderItemStatus
{
    public const string New = "NEW";
    public const string Sent = "SENT";
    public const string Preparing = "PREPARING";
    public const string Ready = "READY";
    public const string Served = "SERVED";
    public const string Cancelled = "CANCELLED";
}

public static class KotStatus
{
    public const string New = "NEW";
    public const string Preparing = "PREPARING";
    public const string Ready = "READY";
    public const string Served = "SERVED";
    public const string Cancelled = "CANCELLED";
}

public static class TableStatus
{
    public const string Available = "AVAILABLE";
    public const string Reserved = "RESERVED";
    public const string Occupied = "OCCUPIED";
    public const string Billing = "BILLING";
    public const string Cleaning = "CLEANING";
    public static readonly string[] All = [Available, Reserved, Occupied, Billing, Cleaning];
}

public static class PaymentMethod
{
    public const string Cash = "CASH";
    public const string Card = "CARD";
    public const string MobileBanking = "MOBILE_BANKING";
    public const string Credit = "CREDIT";
    public const string Other = "OTHER";
    public static readonly string[] All = [Cash, Card, MobileBanking, Credit, Other];
    public static string Label(string m) => m switch
    {
        Cash => "Cash", Card => "Card", MobileBanking => "Mobile banking", Credit => "Customer credit", _ => "Other",
    };
}

public static class DiscountType
{
    public const string None = "NONE";
    public const string Percent = "PERCENT";
    public const string Fixed = "FIXED";
    public const string Coupon = "COUPON";
}
