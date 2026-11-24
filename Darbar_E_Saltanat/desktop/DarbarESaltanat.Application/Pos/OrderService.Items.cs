using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Printing;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Domain.Security;

namespace DarbarESaltanat.Application.Pos;

public sealed partial class OrderService
{
    public const int MaxLineQuantity = 999;

    public async Task<Result<PosOutcome>> AddItemAsync(string orderUuid, AddItemRequest req, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersCreate);
        if (req.Quantity is < 1 or > MaxLineQuantity) return Fail($"Quantity must be between 1 and {MaxLineQuantity}");
        return await MutateAsync(orderUuid, ct, async (uow, ctx, s) =>
        {
            if (ctx.Order.Status == OrderStatus.Held) return "This order is on hold. Resume it before adding items.";
            var item = await uow.FindAsync<MenuItem>(req.MenuItemUuid, ct);
            if (item is null || !item.IsActive) return "Menu item not found";
            if (!item.IsAvailable) return $"{item.Name} is marked out of stock";

            var variants = await uow.ListAsync<ItemVariant>(v => v.MenuItemUuid == item.Uuid && v.IsActive, ct);
            ItemVariant? variant = null;
            if (req.VariantUuid is not null)
            {
                variant = variants.FirstOrDefault(v => v.Uuid == req.VariantUuid);
                if (variant is null) return "The selected size/variant is not available for this item";
            }
            else if (variants.Count > 0)
            {
                variant = variants.FirstOrDefault(v => v.IsDefault);
                if (variant is null) return $"Choose a size for {item.Name}";
            }

            var selections = (req.Addons ?? Array.Empty<AddonSelection>()).Where(a => a.Quantity > 0).ToList();
            var addonRows = new List<OrderItemAddon>();
            var addonsPerUnit = 0m;
            if (selections.Count > 0)
            {
                if (selections.Select(a => a.AddonUuid).Distinct().Count() != selections.Count) return "The same add-on was selected twice";
                var maps = await uow.ListAsync<ItemAddonMapping>(m => m.MenuItemUuid == item.Uuid, ct);
                var ids = selections.Select(a => a.AddonUuid).ToList();
                var addons = await uow.ListAsync<ItemAddon>(a => ids.Contains(a.Uuid) && a.IsActive, ct);
                foreach (var sel in selections)
                {
                    var map = maps.FirstOrDefault(m => m.AddonUuid == sel.AddonUuid);
                    var addon = addons.FirstOrDefault(a => a.Uuid == sel.AddonUuid);
                    if (map is null || addon is null) return $"An add-on is not available for {item.Name}";
                    if (sel.Quantity > map.MaxQty) return $"{addon.Name}: at most {map.MaxQty} allowed";
                    addonsPerUnit += addon.Price * sel.Quantity;
                    addonRows.Add(new OrderItemAddon { AddonUuid = addon.Uuid, AddonName = addon.Name, Quantity = sel.Quantity, UnitPrice = addon.Price });
                }
            }

            var notes = Clean(req.Notes, 255);
            var unitPrice = variant?.Price ?? item.SellingPrice;
            var existing = addonRows.Count == 0
                ? ctx.Items.FirstOrDefault(i => i.Status == OrderItemStatus.New && i.MenuItemUuid == item.Uuid && i.VariantUuid == variant?.Uuid
                                                && i.UnitPrice == unitPrice && i.Notes == notes && !ctx.Addons.Any(a => a.OrderItemUuid == i.Uuid))
                : null;
            if (existing is not null)
            {
                if (existing.Quantity + req.Quantity > MaxLineQuantity) return $"Quantity cannot exceed {MaxLineQuantity}";
                existing.Quantity += req.Quantity;
            }
            else
            {
                var line = new OrderItem
                {
                    OrderUuid = ctx.Order.Uuid, MenuItemUuid = item.Uuid, VariantUuid = variant?.Uuid,
                    ItemName = item.Name, VariantName = variant?.Name, Quantity = req.Quantity,
                    UnitPrice = unitPrice, AddonsTotal = addonsPerUnit,
                    TaxRate = item.TaxRate > 0 ? item.TaxRate : s.VatRate,
                    Status = OrderItemStatus.New, Notes = notes, CreatedAt = clock.UtcNow,
                    BranchUuid = ctx.Order.BranchUuid ?? device.BranchUuid,
                };
                uow.Add(line);
                ctx.Items.Add(line);
                foreach (var a in addonRows)
                {
                    a.OrderItemUuid = line.Uuid;
                    a.BranchUuid = ctx.Order.BranchUuid ?? device.BranchUuid;
                    uow.Add(a);
                    ctx.Addons.Add(a);
                }
            }
            await ReopenIfBilledAsync(uow, ctx, ct);
            return null;
        });
    }

    /// <summary>Change quantity of a line not yet sent to the kitchen (0 removes it).</summary>
    public async Task<Result<PosOutcome>> SetQuantityAsync(string orderUuid, string itemUuid, int quantity, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersCreate);
        if (quantity is < 0 or > MaxLineQuantity) return Fail($"Quantity must be between 0 and {MaxLineQuantity}");
        return await MutateAsync(orderUuid, ct, (uow, ctx, s) =>
        {
            var item = ctx.Items.FirstOrDefault(i => i.Uuid == itemUuid);
            if (item is null) return Task.FromResult<string?>("Item not found on this order");
            if (item.Status != OrderItemStatus.New)
                return Task.FromResult<string?>("This item was already sent to the kitchen. Add a new line, or cancel it with a reason.");
            if (quantity == 0) RemoveLine(uow, ctx, item);
            else item.Quantity = quantity;
            return Task.FromResult<string?>(null);
        });
    }

    public async Task<Result<PosOutcome>> SetItemNoteAsync(string orderUuid, string itemUuid, string? note, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersCreate);
        return await MutateAsync(orderUuid, ct, (uow, ctx, s) =>
        {
            var item = ctx.Items.FirstOrDefault(i => i.Uuid == itemUuid);
            if (item is null) return Task.FromResult<string?>("Item not found on this order");
            if (item.Status != OrderItemStatus.New) return Task.FromResult<string?>("The kitchen already has this item; tell them directly or cancel and re-add it.");
            item.Notes = Clean(note, 255);
            return Task.FromResult<string?>(null);
        });
    }

    /// <summary>Unsent lines are simply removed. Lines the kitchen has need orders.void + a reason, and print a cancellation KOT.</summary>
    public async Task<Result<PosOutcome>> CancelItemAsync(string orderUuid, string itemUuid, string? reason, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersCreate);
        var s = await settings.GetAsync(ct: ct);
        OrderItem? cancelled = null;
        string? station = null;
        var result = await MutateAsync(orderUuid, ct, async (uow, ctx, _) =>
        {
            var item = ctx.Items.FirstOrDefault(i => i.Uuid == itemUuid);
            if (item is null || item.IsCancelled) return "Item not found on this order";
            if (item.Status == OrderItemStatus.New) { RemoveLine(uow, ctx, item); return null; }

            session.Require(Permissions.OrdersVoid);
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) return "Enter a reason for cancelling an item the kitchen already has";
            item.Status = OrderItemStatus.Cancelled;
            item.CancelReason = Clean(reason, 255);
            foreach (var ki in await uow.ListAsync<KotItem>(k => k.OrderItemUuid == item.Uuid, ct)) ki.Status = KotStatus.Cancelled;
            ctx.Order.Status = ctx.ActiveItems.Any() ? WorkingStatus(ctx) : OrderStatus.Open;
            uow.Add(audit.Create("orders.item_cancelled", "order_items", item.Uuid, null, new { order = ctx.Order.OrderNo, item = item.DisplayName, item.Quantity, reason }));
            cancelled = item;
            station = (await StationsAsync(uow, [item], ct))[item.Uuid];
            return null;
        });
        if (!result.Succeeded || cancelled is null) return result;
        var view = result.Value!.View;
        var job = ReceiptFormatter.Kot($"{view.Order.OrderNo}-X", station, view,
            [new ReceiptFormatter.KotLine(cancelled.DisplayName, cancelled.Quantity, view.Lines.First(l => l.ItemUuid == cancelled.Uuid).AddonsText, $"CANCELLED: {cancelled.CancelReason}")],
            s, clock.UtcNow, cancellation: true);
        return Result<PosOutcome>.Ok(result.Value! with { PrintJobs = [job] });
    }

    /// <summary>Creates one KOT per kitchen station for all NEW lines and marks them SENT.</summary>
    public async Task<Result<PosOutcome>> SendToKitchenAsync(string orderUuid, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersCreate);
        var s = await settings.GetAsync(ct: ct);
        var tickets = new List<(Kot Kot, List<OrderItem> Items)>();
        var result = await MutateAsync(orderUuid, ct, async (uow, ctx, _) =>
        {
            if (ctx.Order.Status == OrderStatus.Held) return "Resume the order before sending it to the kitchen";
            var fresh = ctx.Items.Where(i => i.Status == OrderItemStatus.New).ToList();
            if (fresh.Count == 0) return "There are no new items to send to the kitchen";
            var stations = await StationsAsync(uow, fresh, ct);
            var n = await uow.CountAsync<Kot>(k => k.OrderUuid == ctx.Order.Uuid, includeDeleted: true, ct);
            foreach (var group in fresh.GroupBy(i => stations[i.Uuid]).OrderBy(g => g.Key))
            {
                n++;
                var kot = new Kot
                {
                    OrderUuid = ctx.Order.Uuid, KotNo = $"{ctx.Order.OrderNo}-K{n}", Station = group.Key,
                    Status = KotStatus.New, Notes = ctx.Order.Notes, PrintedAt = clock.UtcNow,
                    BranchUuid = ctx.Order.BranchUuid,
                };
                uow.Add(kot);
                foreach (var item in group)
                {
                    uow.Add(new KotItem
                    {
                        KotUuid = kot.Uuid, OrderItemUuid = item.Uuid, ItemName = item.DisplayName,
                        Quantity = item.Quantity, BranchUuid = ctx.Order.BranchUuid
                    });
                    item.Status = OrderItemStatus.Sent;
                }
                tickets.Add((kot, group.ToList()));
            }
            ctx.Order.Status = OrderStatus.InKitchen;
            if (ctx.Order.OrderType == OrderType.DineIn) await SetTablesStatusAsync(uow, ctx.Order, TableStatus.Occupied, ct);
            return null;
        });
        if (!result.Succeeded) return result;
        var view = result.Value!.View;
        var jobs = tickets.Select(t => ReceiptFormatter.Kot(t.Kot.KotNo, t.Kot.Station, view,
            t.Items.Select(i => new ReceiptFormatter.KotLine(i.DisplayName, i.Quantity, view.Lines.First(l => l.ItemUuid == i.Uuid).AddonsText, i.Notes)).ToList(),
            s, clock.UtcNow)).ToList();
        return Result<PosOutcome>.Ok(result.Value! with { PrintJobs = jobs });
    }

    public async Task<Result<PosOutcome>> ApplyDiscountAsync(string orderUuid, DiscountRequest req, CancellationToken ct = default)
    {
        session.Require(Permissions.DiscountsApply);
        return await MutateAsync(orderUuid, ct, async (uow, ctx, s) =>
        {
            var o = ctx.Order;
            var before = new { o.DiscountType, o.DiscountValue, o.DiscountAmount };
            var subtotal = ctx.ActiveItems.Sum(i => Money.Round2((i.UnitPrice + i.AddonsTotal) * i.Quantity - i.DiscountAmount));
            if (req.Type != DiscountType.None && subtotal <= 0) return "Add items before applying a discount";

            var type = req.Type;
            var value = req.Value;
            string? couponUuid = null;
            var overrideChecked = false;

            if (req.PresetUuid is not null)
            {
                var preset = await uow.FindAsync<Discount>(req.PresetUuid, ct);
                if (preset is null || !preset.IsActive) return "Discount not found";
                if (subtotal < preset.MinOrder) return $"{preset.Name} needs a minimum order of {Money.Format(preset.MinOrder)}";
                if (preset.RequiresPermission) { session.Require(Permissions.DiscountsOverride); overrideChecked = true; }
                type = preset.Type;
                value = preset.Value;
                if (preset.MaxDiscount is { } cap && PricingEngine.DiscountAmount(new OrderDiscount(type, value), subtotal) > cap)
                { type = DiscountType.Fixed; value = cap; }
            }
            else if (type == DiscountType.Coupon)
            {
                var code = (req.CouponCode ?? "").Trim().ToUpperInvariant();
                if (code.Length == 0) return "Enter the coupon code";
                var coupon = (await uow.ListAsync<Coupon>(c => c.Code == code, ct)).FirstOrDefault();
                var localUnsynced = coupon is null ? 0 : await uow.CountAsync<CouponUsage>(u => u.CouponUuid == coupon.Uuid && u.SyncStatus != RowSyncStatus.Synced, ct: ct);
                var customerUses = coupon is null || o.CustomerUuid is null ? 0
                    : await uow.CountAsync<CouponUsage>(u => u.CouponUuid == coupon.Uuid && u.CustomerUuid == o.CustomerUuid, ct: ct);
                var check = CouponRules.Evaluate(coupon, subtotal, clock.UtcNow, o.CustomerUuid is not null, customerUses, localUnsynced);
                if (!check.Valid) return check.Reason;
                couponUuid = coupon!.Uuid;
                value = coupon.Value;
                overrideChecked = true; // a valid coupon is pre-authorised
            }
            else if (type == DiscountType.Percent)
            {
                if (value is <= 0 or > 100) return "Discount percentage must be between 0 and 100";
            }
            else if (type == DiscountType.Fixed)
            {
                if (value <= 0) return "Discount amount must be greater than zero";
                if (value > subtotal) return $"Discount cannot exceed the subtotal {Money.Format(subtotal)}";
                if (!Money.HasAtMostTwoDecimals(value)) return "Discount can have at most 2 decimal places";
            }
            else if (type != DiscountType.None) return "Unknown discount type";

            if (type is DiscountType.Percent or DiscountType.Fixed && !overrideChecked)
            {
                var amount = PricingEngine.DiscountAmount(new OrderDiscount(type, value), subtotal);
                if (amount / subtotal * 100m > s.MaxDiscountPercentWithoutPermission + 0.0001m) session.Require(Permissions.DiscountsOverride);
            }

            o.DiscountType = type;
            o.DiscountValue = type == DiscountType.None ? 0 : value;
            o.CouponUuid = couponUuid;
            uow.Add(audit.Create("orders.discount", "orders", o.Uuid, before, new { o.OrderNo, type, value, coupon = req.CouponCode, preset = req.PresetUuid }));
            return null;
        });
    }

    public async Task<IReadOnlyList<Discount>> ListPresetDiscountsAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.DiscountsApply);
        await using var uow = uowFactory.Begin();
        return (await uow.ListAsync<Discount>(d => d.IsActive, ct)).OrderBy(d => d.Name).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private static void RemoveLine(IUnitOfWork uow, OrderContext ctx, OrderItem item)
    {
        foreach (var a in ctx.Addons.Where(a => a.OrderItemUuid == item.Uuid).ToList()) { uow.Delete(a); ctx.Addons.Remove(a); }
        uow.Delete(item);
        ctx.Items.Remove(item);
    }

    /// <summary>Adding to a billed order puts it back to work and the table back to OCCUPIED.</summary>
    private static async Task ReopenIfBilledAsync(IUnitOfWork uow, OrderContext ctx, CancellationToken ct)
    {
        if (ctx.Order.Status is OrderStatus.Billed or OrderStatus.Served or OrderStatus.Ready)
        {
            ctx.Order.Status = WorkingStatus(ctx);
            if (ctx.Order.OrderType == OrderType.DineIn) await SetTablesStatusAsync(uow, ctx.Order, TableStatus.Occupied, ct);
        }
    }

    /// <summary>Kitchen station per order line: the item's category station, else "Main Kitchen".</summary>
    private static async Task<Dictionary<string, string>> StationsAsync(IUnitOfWork uow, IReadOnlyList<OrderItem> items, CancellationToken ct)
    {
        var menuIds = items.Select(i => i.MenuItemUuid).Distinct().ToList();
        var menu = await uow.ListAsync<MenuItem>(m => menuIds.Contains(m.Uuid), ct);
        var catIds = menu.Select(m => m.CategoryUuid).Distinct().ToList();
        var cats = await uow.ListAsync<Category>(c => catIds.Contains(c.Uuid), ct);
        return items.ToDictionary(i => i.Uuid, i =>
        {
            var m = menu.FirstOrDefault(x => x.Uuid == i.MenuItemUuid);
            var c = m is null ? null : cats.FirstOrDefault(x => x.Uuid == m.CategoryUuid);
            return string.IsNullOrWhiteSpace(c?.KitchenStation) ? "Main Kitchen" : c!.KitchenStation!;
        });
    }
}
