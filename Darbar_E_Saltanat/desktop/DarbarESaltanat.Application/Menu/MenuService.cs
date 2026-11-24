using System.Globalization;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Security;
using FluentValidation;

namespace DarbarESaltanat.Application.Menu;

public sealed record CategoryInput(string? Uuid, string Name, string? KitchenStation, int SortOrder, bool IsActive = true, string? Description = null);

public sealed record MenuItemInput(
    string? Uuid, string CategoryUuid, string Name, string? NameBn, string? Description, string? Sku, string? Barcode,
    decimal CostPrice, decimal SellingPrice, decimal TaxRate, int PrepTimeMinutes, int SortOrder, bool IsAvailable, bool IsActive, string? ImageUrl = null);

public sealed record VariantInput(string? Uuid, string Name, decimal Price, decimal CostPrice, bool IsDefault, string? Sku = null);
public sealed record AddonInput(string? Uuid, string Name, string? GroupName, decimal Price, bool IsActive = true);
public sealed record ItemAddonLink(string AddonUuid, int MaxQty);

/// <summary>Everything the POS needs to show the menu, loaded once into memory for instant search.</summary>
public sealed record PosCatalog(
    IReadOnlyList<Category> Categories, IReadOnlyList<MenuItem> Items, IReadOnlyList<ItemVariant> Variants,
    IReadOnlyList<ItemAddon> Addons, IReadOnlyList<ItemAddonMapping> Mappings)
{
    public IEnumerable<ItemVariant> VariantsOf(string itemUuid) => Variants.Where(v => v.MenuItemUuid == itemUuid);
    public IEnumerable<(ItemAddon Addon, int MaxQty)> AddonsOf(string itemUuid) =>
        Mappings.Where(m => m.MenuItemUuid == itemUuid)
            .Select(m => (Addons.FirstOrDefault(a => a.Uuid == m.AddonUuid), m.MaxQty))
            .Where(x => x.Item1 is not null).Select(x => (x.Item1!, x.MaxQty));
    public bool HasOptions(string itemUuid) => VariantsOf(itemUuid).Count() > 1 || Mappings.Any(m => m.MenuItemUuid == itemUuid);
}

public sealed record ImportReport(int Created, int Updated, IReadOnlyList<string> Errors);

public sealed class MenuItemInputValidator : AbstractValidator<MenuItemInput>
{
    public MenuItemInputValidator()
    {
        RuleFor(x => x.CategoryUuid).NotEmpty().WithMessage("Choose a category");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter the item name").MinimumLength(2).MaximumLength(150);
        RuleFor(x => x.NameBn).MaximumLength(150);
        RuleFor(x => x.Sku).MaximumLength(60);
        RuleFor(x => x.Barcode).MaximumLength(60);
        RuleFor(x => x.SellingPrice).GreaterThanOrEqualTo(0).LessThan(10_000_000).Must(Money.HasAtMostTwoDecimals).WithMessage("Enter a valid selling price");
        RuleFor(x => x.CostPrice).GreaterThanOrEqualTo(0).LessThan(10_000_000).Must(Money.HasAtMostTwoDecimals).WithMessage("Enter a valid cost price");
        RuleFor(x => x.TaxRate).InclusiveBetween(0, 100).WithMessage("Tax rate must be 0–100 (0 = restaurant VAT rate)");
        RuleFor(x => x.PrepTimeMinutes).InclusiveBetween(0, 600);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 100000);
    }
}

public sealed class MenuService(IUnitOfWorkFactory uowFactory, ICurrentSession session, AuditFactory audit)
{
    private readonly MenuItemInputValidator _itemValidator = new();
    public event EventHandler? MenuChanged;

    // ------------------------------------------------------------------ reads

    public async Task<PosCatalog> GetCatalogAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuView);
        await using var uow = uowFactory.Begin();
        var cats = await uow.ListAsync<Category>(c => includeInactive || c.IsActive, ct);
        var items = await uow.ListAsync<MenuItem>(i => includeInactive || i.IsActive, ct);
        var variants = await uow.ListAsync<ItemVariant>(v => includeInactive || v.IsActive, ct);
        var addons = await uow.ListAsync<ItemAddon>(a => includeInactive || a.IsActive, ct);
        var maps = await uow.ListAsync<ItemAddonMapping>(null, ct);
        return new PosCatalog(
            cats.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList(),
            items.OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToList(),
            variants.OrderBy(v => v.Price).ToList(),
            addons.OrderBy(a => a.GroupName).ThenBy(a => a.Name).ToList(), maps);
    }

    // ------------------------------------------------------------------ categories

    public async Task<Result<Category>> SaveCategoryAsync(CategoryInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuManage);
        var name = input.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 120) return Result<Category>.Fail("Category name must be 2–120 characters");
        if (input.SortOrder is < 0 or > 100000) return Result<Category>.Fail("Sort order must be 0–100000");
        await using var uow = uowFactory.Begin();
        if ((await uow.ListAsync<Category>(c => c.Name == name && c.Uuid != input.Uuid, ct)).Count > 0) return Result<Category>.Fail($"Category {name} already exists");
        Category cat;
        if (input.Uuid is null) { cat = new Category(); uow.Add(cat); }
        else cat = await uow.FindAsync<Category>(input.Uuid, ct) ?? throw new InvalidOperationException("Category not found");
        cat.Name = name;
        cat.KitchenStation = string.IsNullOrWhiteSpace(input.KitchenStation) ? null : input.KitchenStation.Trim();
        cat.SortOrder = input.SortOrder;
        cat.IsActive = input.IsActive;
        cat.Description = input.Description?.Trim();
        uow.Add(audit.Create(input.Uuid is null ? "categories.create" : "categories.update", "categories", cat.Uuid, null, input));
        await uow.CommitAsync(ct);
        MenuChanged?.Invoke(this, EventArgs.Empty);
        return Result<Category>.Ok(cat);
    }

    public async Task<Result> DeleteCategoryAsync(string uuid, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuManage);
        await using var uow = uowFactory.Begin();
        var cat = await uow.FindAsync<Category>(uuid, ct);
        if (cat is null) return Result.Fail("Category not found");
        var n = await uow.CountAsync<MenuItem>(i => i.CategoryUuid == uuid, ct: ct);
        if (n > 0) return Result.Fail($"{cat.Name} still has {n} item(s). Move or delete them first.");
        uow.Delete(cat);
        uow.Add(audit.Create("categories.delete", "categories", uuid, new { cat.Name }));
        await uow.CommitAsync(ct);
        MenuChanged?.Invoke(this, EventArgs.Empty);
        return Result.Ok();
    }

    // ------------------------------------------------------------------ items

    public async Task<Result<MenuItem>> SaveItemAsync(MenuItemInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuManage);
        var v = _itemValidator.Validate(input);
        if (!v.IsValid) return Result<MenuItem>.Fail(string.Join(Environment.NewLine, v.Errors.Select(e => e.ErrorMessage)), "VALIDATION");
        await using var uow = uowFactory.Begin();
        var r = await UpsertItemAsync(uow, input, ct);
        if (!r.Succeeded) return r;
        await uow.CommitAsync(ct);
        MenuChanged?.Invoke(this, EventArgs.Empty);
        return r;
    }

    private async Task<Result<MenuItem>> UpsertItemAsync(IUnitOfWork uow, MenuItemInput input, CancellationToken ct)
    {
        if (await uow.FindAsync<Category>(input.CategoryUuid, ct) is null) return Result<MenuItem>.Fail("Category not found");
        string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var sku = Norm(input.Sku); var barcode = Norm(input.Barcode);
        if (sku is not null && (await uow.ListAsync<MenuItem>(i => i.Sku == sku && i.Uuid != input.Uuid, ct)).Count > 0)
            return Result<MenuItem>.Fail($"Another item already uses SKU {sku}");
        if (barcode is not null && (await uow.ListAsync<MenuItem>(i => i.Barcode == barcode && i.Uuid != input.Uuid, ct)).Count > 0)
            return Result<MenuItem>.Fail($"Another item already uses barcode {barcode}");

        MenuItem item;
        object? before = null;
        if (input.Uuid is null) { item = new MenuItem(); uow.Add(item); }
        else
        {
            item = await uow.FindAsync<MenuItem>(input.Uuid, ct) ?? throw new InvalidOperationException("Item not found");
            before = new { item.Name, item.SellingPrice, item.CostPrice, item.IsAvailable, item.TaxRate };
        }
        item.CategoryUuid = input.CategoryUuid; item.Name = input.Name.Trim(); item.NameBn = Norm(input.NameBn);
        item.Description = Norm(input.Description); item.Sku = sku; item.Barcode = barcode; item.ImageUrl = Norm(input.ImageUrl) ?? item.ImageUrl;
        item.CostPrice = input.CostPrice; item.SellingPrice = input.SellingPrice; item.TaxRate = input.TaxRate;
        item.PrepTimeMinutes = input.PrepTimeMinutes; item.SortOrder = input.SortOrder; item.IsAvailable = input.IsAvailable; item.IsActive = input.IsActive;
        uow.Add(audit.Create(input.Uuid is null ? "menu_items.create" : "menu_items.update", "menu_items", item.Uuid, before,
            new { item.Name, item.SellingPrice, item.CostPrice, item.IsAvailable, item.TaxRate }));
        return Result<MenuItem>.Ok(item);
    }

    public async Task<Result> SetAvailabilityAsync(string itemUuid, bool available, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuManage);
        await using var uow = uowFactory.Begin();
        var item = await uow.FindAsync<MenuItem>(itemUuid, ct);
        if (item is null) return Result.Fail("Item not found");
        item.IsAvailable = available;
        uow.Add(audit.Create("menu_items.availability", "menu_items", itemUuid, null, new { item.Name, available }));
        await uow.CommitAsync(ct);
        MenuChanged?.Invoke(this, EventArgs.Empty);
        return Result.Ok();
    }

    public async Task<Result> DeleteItemAsync(string itemUuid, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuManage);
        await using var uow = uowFactory.Begin();
        var item = await uow.FindAsync<MenuItem>(itemUuid, ct);
        if (item is null) return Result.Fail("Item not found");
        foreach (var v in await uow.ListAsync<ItemVariant>(x => x.MenuItemUuid == itemUuid, ct)) uow.Delete(v);
        foreach (var m in await uow.ListAsync<ItemAddonMapping>(x => x.MenuItemUuid == itemUuid, ct)) uow.Delete(m);
        uow.Delete(item);
        uow.Add(audit.Create("menu_items.delete", "menu_items", itemUuid, new { item.Name }));
        await uow.CommitAsync(ct);
        MenuChanged?.Invoke(this, EventArgs.Empty);
        return Result.Ok();
    }

    /// <summary>Replaces an item's variants: updates matching uuids, adds new, soft-deletes the rest.</summary>
    public async Task<Result> SaveVariantsAsync(string itemUuid, IReadOnlyList<VariantInput> variants, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuManage);
        var list = variants.Where(v => !string.IsNullOrWhiteSpace(v.Name)).ToList();
        if (list.Select(v => v.Name.Trim().ToLowerInvariant()).Distinct().Count() != list.Count) return Result.Fail("Variant names must be unique");
        if (list.Any(v => v.Price < 0 || v.CostPrice < 0 || !Money.HasAtMostTwoDecimals(v.Price))) return Result.Fail("Variant prices must be valid amounts");
        if (list.Any(v => v.Name.Trim().Length > 60)) return Result.Fail("Variant names can be at most 60 characters");
        if (list.Count(v => v.IsDefault) > 1) return Result.Fail("Only one variant can be the default");
        await using var uow = uowFactory.Begin();
        if (await uow.FindAsync<MenuItem>(itemUuid, ct) is null) return Result.Fail("Item not found");
        var existing = await uow.ListAsync<ItemVariant>(v => v.MenuItemUuid == itemUuid, ct);
        foreach (var gone in existing.Where(e => list.All(v => v.Uuid != e.Uuid))) uow.Delete(gone);
        var hasDefault = list.Any(v => v.IsDefault);
        for (var i = 0; i < list.Count; i++)
        {
            var input = list[i];
            var row = input.Uuid is null ? null : existing.FirstOrDefault(e => e.Uuid == input.Uuid);
            if (row is null) { row = new ItemVariant { MenuItemUuid = itemUuid }; uow.Add(row); }
            row.Name = input.Name.Trim(); row.Price = input.Price; row.CostPrice = input.CostPrice;
            row.Sku = string.IsNullOrWhiteSpace(input.Sku) ? null : input.Sku.Trim();
            row.IsDefault = hasDefault ? input.IsDefault : i == 0;
            row.IsActive = true;
        }
        uow.Add(audit.Create("menu_items.variants", "menu_items", itemUuid, null, list));
        await uow.CommitAsync(ct);
        MenuChanged?.Invoke(this, EventArgs.Empty);
        return Result.Ok();
    }

    // ------------------------------------------------------------------ add-ons

    public async Task<Result<ItemAddon>> SaveAddonAsync(AddonInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuManage);
        var name = input.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 100) return Result<ItemAddon>.Fail("Add-on name must be 2–100 characters");
        if (input.Price < 0 || !Money.HasAtMostTwoDecimals(input.Price)) return Result<ItemAddon>.Fail("Enter a valid add-on price");
        await using var uow = uowFactory.Begin();
        ItemAddon a;
        if (input.Uuid is null) { a = new ItemAddon(); uow.Add(a); }
        else a = await uow.FindAsync<ItemAddon>(input.Uuid, ct) ?? throw new InvalidOperationException("Add-on not found");
        a.Name = name; a.GroupName = string.IsNullOrWhiteSpace(input.GroupName) ? null : input.GroupName.Trim(); a.Price = input.Price; a.IsActive = input.IsActive;
        uow.Add(audit.Create(input.Uuid is null ? "item_addons.create" : "item_addons.update", "item_addons", a.Uuid, null, input));
        await uow.CommitAsync(ct);
        MenuChanged?.Invoke(this, EventArgs.Empty);
        return Result<ItemAddon>.Ok(a);
    }

    public async Task<Result> SetItemAddonsAsync(string itemUuid, IReadOnlyList<ItemAddonLink> links, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuManage);
        if (links.Any(l => l.MaxQty is < 1 or > 20)) return Result.Fail("Max quantity per add-on must be 1–20");
        await using var uow = uowFactory.Begin();
        if (await uow.FindAsync<MenuItem>(itemUuid, ct) is null) return Result.Fail("Item not found");
        var current = await uow.ListAsync<ItemAddonMapping>(m => m.MenuItemUuid == itemUuid, ct);
        foreach (var m in current.Where(m => links.All(l => l.AddonUuid != m.AddonUuid))) uow.Delete(m);
        foreach (var l in links.DistinctBy(l => l.AddonUuid))
        {
            var m = current.FirstOrDefault(x => x.AddonUuid == l.AddonUuid);
            if (m is null) uow.Add(new ItemAddonMapping { MenuItemUuid = itemUuid, AddonUuid = l.AddonUuid, MaxQty = l.MaxQty });
            else m.MaxQty = l.MaxQty;
        }
        uow.Add(audit.Create("menu_items.addons", "menu_items", itemUuid, null, links));
        await uow.CommitAsync(ct);
        MenuChanged?.Invoke(this, EventArgs.Empty);
        return Result.Ok();
    }

    // ------------------------------------------------------------------ CSV

    public static readonly string[] CsvColumns =
        ["sku", "name", "name_bn", "category", "selling_price", "cost_price", "tax_rate", "barcode", "prep_time_minutes", "is_available", "description"];

    public async Task<string> ExportCsvAsync(CancellationToken ct = default)
    {
        var cat = await GetCatalogAsync(includeInactive: false, ct);
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<IEnumerable<string?>> { CsvColumns };
        rows.AddRange(cat.Items.Select(i => new[]
        {
            i.Sku, i.Name, i.NameBn, cat.Categories.FirstOrDefault(c => c.Uuid == i.CategoryUuid)?.Name,
            i.SellingPrice.ToString("0.00", inv), i.CostPrice.ToString("0.00", inv), i.TaxRate.ToString("0.##", inv),
            i.Barcode, i.PrepTimeMinutes.ToString(inv), i.IsAvailable ? "yes" : "no", i.Description,
        }));
        return Csv.Write(rows);
    }

    /// <summary>Imports items: matched by SKU, else by name. Unknown categories are created. All-or-nothing.</summary>
    public async Task<Result<ImportReport>> ImportCsvAsync(string csvText, CancellationToken ct = default)
    {
        session.Require(Permissions.MenuManage);
        var rows = Csv.Parse(csvText);
        if (rows.Count < 2) return Result<ImportReport>.Fail("The file has no data rows");
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string n) => header.IndexOf(n);
        if (Col("name") < 0 || Col("category") < 0 || Col("selling_price") < 0) return Result<ImportReport>.Fail("Required columns: name, category, selling_price");
        var inv = CultureInfo.InvariantCulture;
        string? Get(List<string> r, string n) { var i = Col(n); return i >= 0 && i < r.Count && r[i].Trim().Length > 0 ? r[i].Trim() : null; }
        decimal Dec(List<string> r, string n, decimal d) => Get(r, n) is { } s && decimal.TryParse(s.Replace("৳", "").Replace(",", ""), NumberStyles.Number, inv, out var x) ? x : d;

        var errors = new List<string>();
        int created = 0, updated = 0;
        await using var uow = uowFactory.Begin();
        var cats = await uow.ListAsync<Category>(null, ct);
        var items = await uow.ListAsync<MenuItem>(null, ct);
        var seenSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; n < rows.Count; n++)
        {
            var r = rows[n];
            var name = Get(r, "name"); var catName = Get(r, "category");
            if (name is null || catName is null) { errors.Add($"Row {n + 1}: name and category are required"); continue; }
            if (Get(r, "selling_price") is not { } priceText || !decimal.TryParse(priceText.Replace(",", ""), NumberStyles.Number, inv, out var price))
            { errors.Add($"Row {n + 1} ({name}): invalid selling_price"); continue; }
            var cat = cats.FirstOrDefault(c => c.Name.Equals(catName, StringComparison.OrdinalIgnoreCase));
            if (cat is null) { cat = new Category { Name = catName, SortOrder = cats.Count + 1 }; uow.Add(cat); cats.Add(cat); }
            var sku = Get(r, "sku");
            if (sku is not null && !seenSkus.Add(sku)) { errors.Add($"Row {n + 1} ({name}): SKU {sku} appears more than once in the file"); continue; }
            var existing = sku is not null ? items.FirstOrDefault(i => i.Sku == sku) : items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var avail = Get(r, "is_available")?.ToLowerInvariant();
            var input = new MenuItemInput(existing?.Uuid, cat.Uuid, name, Get(r, "name_bn"), Get(r, "description"), sku, Get(r, "barcode"),
                Dec(r, "cost_price", existing?.CostPrice ?? 0), price, Dec(r, "tax_rate", existing?.TaxRate ?? 0),
                (int)Dec(r, "prep_time_minutes", existing?.PrepTimeMinutes ?? 15), existing?.SortOrder ?? n,
                avail is null ? existing?.IsAvailable ?? true : avail is "yes" or "true" or "1" or "y", true);
            var v = _itemValidator.Validate(input);
            if (!v.IsValid) { errors.Add($"Row {n + 1} ({name}): {v.Errors[0].ErrorMessage}"); continue; }
            var res = await UpsertItemAsync(uow, input, ct);
            if (!res.Succeeded) { errors.Add($"Row {n + 1} ({name}): {res.Error}"); continue; }
            if (existing is null) { created++; items.Add(res.Value!); } else updated++;
        }
        if (errors.Count > 0) return Result<ImportReport>.Fail($"Nothing was imported. Fix these rows and try again:{Environment.NewLine}{string.Join(Environment.NewLine, errors.Take(20))}");
        await uow.CommitAsync(ct);
        MenuChanged?.Invoke(this, EventArgs.Empty);
        return Result<ImportReport>.Ok(new ImportReport(created, updated, errors));
    }
}
