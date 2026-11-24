using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Menu;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Domain.Entities;
using Microsoft.Win32;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed partial class VariantRow : ObservableObject
{
    public string? Uuid { get; init; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _price = "0";
    [ObservableProperty] private string _costPrice = "0";
    [ObservableProperty] private bool _isDefault;
}

public sealed partial class AddonRow : ObservableObject
{
    public required ItemAddon Addon { get; init; }
    public string Label => $"{Addon.Name} (+{Money.Format(Addon.Price)}){(Addon.GroupName is null ? "" : $" · {Addon.GroupName}")}";
    [ObservableProperty] private bool _isLinked;
    [ObservableProperty] private string _maxQty = "1";
}

public sealed record ItemRow(MenuItem Item, string CategoryName)
{
    public string Name => Item.Name;
    public string Price => Money.Format(Item.SellingPrice);
    public string Status => !Item.IsActive ? "Inactive" : Item.IsAvailable ? "Available" : "Out of stock";
    public string? Sku => Item.Sku;
}

/// <summary>Menu management: categories, items, variants, add-ons, availability, CSV import/export.</summary>
public sealed partial class MenuManagementViewModel(MenuService menu, DialogService dialogs) : ObservableObject, IPage
{
    private PosCatalog? _catalog;
    public ObservableCollection<Category> Categories { get; } = [];
    public ObservableCollection<ItemRow> Items { get; } = [];
    public ObservableCollection<VariantRow> Variants { get; } = [];
    public ObservableCollection<AddonRow> Addons { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private ItemRow? _selectedItem;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _hasItem;

    // ---- item form
    [ObservableProperty] private string? _editUuid;
    [ObservableProperty] private Category? _formCategory;
    [ObservableProperty] private string _formName = "";
    [ObservableProperty] private string _formNameBn = "";
    [ObservableProperty] private string _formSku = "";
    [ObservableProperty] private string _formBarcode = "";
    [ObservableProperty] private string _formPrice = "";
    [ObservableProperty] private string _formCost = "0";
    [ObservableProperty] private string _formTaxRate = "0";
    [ObservableProperty] private string _formPrepTime = "15";
    [ObservableProperty] private string _formSortOrder = "0";
    [ObservableProperty] private string _formDescription = "";
    [ObservableProperty] private bool _formAvailable = true;
    [ObservableProperty] private bool _formActive = true;

    public async Task LoadAsync(object? argument) => await ReloadAsync(null);

    private async Task ReloadAsync(string? keepItemUuid)
    {
        _catalog = await menu.GetCatalogAsync(includeInactive: true);
        var catUuid = SelectedCategory?.Uuid;
        Categories.Clear();
        foreach (var c in _catalog.Categories) Categories.Add(c);
        SelectedCategory = Categories.FirstOrDefault(c => c.Uuid == catUuid) ?? Categories.FirstOrDefault();
        FillItems();
        Addons.Clear();
        foreach (var a in _catalog.Addons) Addons.Add(new AddonRow { Addon = a });
        if (keepItemUuid is not null) SelectedItem = Items.FirstOrDefault(i => i.Item.Uuid == keepItemUuid);
    }

    partial void OnSelectedCategoryChanged(Category? value) => FillItems();

    private void FillItems()
    {
        Items.Clear();
        if (_catalog is null || SelectedCategory is null) return;
        foreach (var i in _catalog.Items.Where(i => i.CategoryUuid == SelectedCategory.Uuid)) Items.Add(new ItemRow(i, SelectedCategory.Name));
    }

    partial void OnSelectedItemChanged(ItemRow? value)
    {
        if (value is null) return;
        var i = value.Item;
        EditUuid = i.Uuid; FormCategory = Categories.FirstOrDefault(c => c.Uuid == i.CategoryUuid);
        FormName = i.Name; FormNameBn = i.NameBn ?? ""; FormSku = i.Sku ?? ""; FormBarcode = i.Barcode ?? "";
        FormPrice = i.SellingPrice.ToString("0.00", CultureInfo.InvariantCulture); FormCost = i.CostPrice.ToString("0.00", CultureInfo.InvariantCulture);
        FormTaxRate = i.TaxRate.ToString("0.##", CultureInfo.InvariantCulture); FormPrepTime = i.PrepTimeMinutes.ToString(CultureInfo.InvariantCulture);
        FormSortOrder = i.SortOrder.ToString(CultureInfo.InvariantCulture); FormDescription = i.Description ?? "";
        FormAvailable = i.IsAvailable; FormActive = i.IsActive;
        Variants.Clear();
        foreach (var v in _catalog!.Variants.Where(v => v.MenuItemUuid == i.Uuid))
            Variants.Add(new VariantRow { Uuid = v.Uuid, Name = v.Name, Price = v.Price.ToString("0.00", CultureInfo.InvariantCulture), CostPrice = v.CostPrice.ToString("0.00", CultureInfo.InvariantCulture), IsDefault = v.IsDefault });
        foreach (var a in Addons)
        {
            var m = _catalog.Mappings.FirstOrDefault(x => x.MenuItemUuid == i.Uuid && x.AddonUuid == a.Addon.Uuid);
            a.IsLinked = m is not null; a.MaxQty = (m?.MaxQty ?? 1).ToString(CultureInfo.InvariantCulture);
        }
        HasItem = true;
    }

    private void Show(string? msg, bool error = false) { Message = msg; IsError = error; }
    private static bool Dec(string s, out decimal d) => decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out d);
    private static bool Int(string s, out int i) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i);

    // ------------------------------------------------------------------ categories

    [RelayCommand]
    private async Task NewCategoryAsync()
    {
        var name = dialogs.Prompt("New category", "Category name", validate: s => s.Length is >= 2 and <= 120 ? null : "2–120 characters");
        if (name is null) return;
        var station = dialogs.Prompt("New category", "Kitchen station for its KOTs (e.g. Main Kitchen, Grill, Tandoor, Bar)", "Main Kitchen");
        if (station is null) return;
        var r = await menu.SaveCategoryAsync(new CategoryInput(null, name, station, Categories.Count + 1));
        Show(r.Succeeded ? $"Category {name} created" : r.Error, !r.Succeeded);
        if (r.Succeeded) { await ReloadAsync(null); SelectedCategory = Categories.FirstOrDefault(c => c.Uuid == r.Value!.Uuid); }
    }

    [RelayCommand]
    private async Task EditCategoryAsync()
    {
        if (SelectedCategory is not { } c) return;
        var name = dialogs.Prompt("Edit category", "Category name", c.Name, validate: s => s.Length is >= 2 and <= 120 ? null : "2–120 characters");
        if (name is null) return;
        var station = dialogs.Prompt("Edit category", "Kitchen station", c.KitchenStation ?? "Main Kitchen");
        if (station is null) return;
        var order = dialogs.Prompt("Edit category", "Sort order (lower shows first)", c.SortOrder.ToString(), validate: s => Int(s, out var n) && n >= 0 ? null : "Whole number ≥ 0");
        if (order is null) return;
        var active = dialogs.Confirm($"Show {name} on the POS? (No = hide the whole category)", "Category active");
        var r = await menu.SaveCategoryAsync(new CategoryInput(c.Uuid, name, station, int.Parse(order), active, c.Description));
        Show(r.Succeeded ? "Category saved" : r.Error, !r.Succeeded);
        if (r.Succeeded) await ReloadAsync(null);
    }

    [RelayCommand]
    private async Task DeleteCategoryAsync()
    {
        if (SelectedCategory is not { } c || !dialogs.Confirm($"Delete category {c.Name}?")) return;
        var r = await menu.DeleteCategoryAsync(c.Uuid);
        Show(r.Succeeded ? "Category deleted" : r.Error, !r.Succeeded);
        if (r.Succeeded) await ReloadAsync(null);
    }

    // ------------------------------------------------------------------ items

    [RelayCommand]
    private void NewItem()
    {
        SelectedItem = null;
        EditUuid = null; FormCategory = SelectedCategory; FormName = ""; FormNameBn = ""; FormSku = ""; FormBarcode = "";
        FormPrice = ""; FormCost = "0"; FormTaxRate = "0"; FormPrepTime = "15"; FormSortOrder = (Items.Count + 1).ToString(); FormDescription = "";
        FormAvailable = true; FormActive = true;
        Variants.Clear();
        foreach (var a in Addons) { a.IsLinked = false; a.MaxQty = "1"; }
        HasItem = true;
        Show("Fill in the new item and press Save item.");
    }

    [RelayCommand]
    private async Task SaveItemAsync()
    {
        if (FormCategory is null) { Show("Choose a category", true); return; }
        if (!Dec(FormPrice, out var price)) { Show("Enter the selling price (e.g. 450 or 450.50)", true); return; }
        if (!Dec(FormCost, out var cost)) { Show("Enter a valid cost price", true); return; }
        if (!Dec(FormTaxRate, out var tax)) { Show("Enter a valid tax rate (0 = restaurant VAT)", true); return; }
        if (!Int(FormPrepTime, out var prep) || !Int(FormSortOrder, out var sort)) { Show("Prep time and sort order must be whole numbers", true); return; }
        var r = await menu.SaveItemAsync(new MenuItemInput(EditUuid, FormCategory.Uuid, FormName, FormNameBn, FormDescription, FormSku, FormBarcode,
            cost, price, tax, prep, sort, FormAvailable, FormActive));
        if (!r.Succeeded) { Show(r.Error, true); return; }
        var uuid = r.Value!.Uuid;
        Show($"{r.Value.Name} saved");
        if (SelectedCategory?.Uuid != FormCategory.Uuid) SelectedCategory = FormCategory;
        await ReloadAsync(uuid);
    }

    [RelayCommand]
    private async Task ToggleAvailabilityAsync(ItemRow? row)
    {
        row ??= SelectedItem;
        if (row is null) return;
        var r = await menu.SetAvailabilityAsync(row.Item.Uuid, !row.Item.IsAvailable);
        Show(r.Succeeded ? $"{row.Name} is now {(row.Item.IsAvailable ? "out of stock" : "available")}" : r.Error, !r.Succeeded);
        await ReloadAsync(row.Item.Uuid);
    }

    [RelayCommand]
    private async Task DeleteItemAsync()
    {
        if (EditUuid is not { } id || !dialogs.Confirm($"Delete {FormName}? It disappears from the POS; past orders are kept.")) return;
        var r = await menu.DeleteItemAsync(id);
        Show(r.Succeeded ? "Item deleted" : r.Error, !r.Succeeded);
        if (r.Succeeded) { HasItem = false; await ReloadAsync(null); }
    }

    // ------------------------------------------------------------------ variants & add-ons

    [RelayCommand] private void AddVariant() => Variants.Add(new VariantRow { Name = Variants.Count == 0 ? "Half" : "Full", Price = FormPrice, CostPrice = FormCost, IsDefault = Variants.Count == 0 });
    [RelayCommand] private void RemoveVariant(VariantRow? row) { if (row is not null) Variants.Remove(row); }

    [RelayCommand]
    private async Task SaveVariantsAsync()
    {
        if (EditUuid is not { } id) { Show("Save the item first", true); return; }
        var list = new List<VariantInput>();
        foreach (var v in Variants)
        {
            if (!Dec(v.Price, out var p) || !Dec(v.CostPrice, out var c)) { Show($"Variant {v.Name}: enter valid prices", true); return; }
            list.Add(new VariantInput(v.Uuid, v.Name, p, c, v.IsDefault));
        }
        var r = await menu.SaveVariantsAsync(id, list);
        Show(r.Succeeded ? "Variants saved" : r.Error, !r.Succeeded);
        if (r.Succeeded) await ReloadAsync(id);
    }

    [RelayCommand]
    private async Task SaveAddonLinksAsync()
    {
        if (EditUuid is not { } id) { Show("Save the item first", true); return; }
        var links = new List<ItemAddonLink>();
        foreach (var a in Addons.Where(a => a.IsLinked))
        {
            if (!Int(a.MaxQty, out var max)) { Show($"{a.Addon.Name}: max quantity must be a whole number", true); return; }
            links.Add(new ItemAddonLink(a.Addon.Uuid, max));
        }
        var r = await menu.SetItemAddonsAsync(id, links);
        Show(r.Succeeded ? "Add-ons saved" : r.Error, !r.Succeeded);
        if (r.Succeeded) await ReloadAsync(id);
    }

    [RelayCommand]
    private async Task NewAddonAsync()
    {
        var name = dialogs.Prompt("New add-on", "Name (e.g. Extra Egg)", validate: s => s.Length is >= 2 and <= 100 ? null : "2–100 characters");
        if (name is null) return;
        var price = dialogs.Prompt("New add-on", "Price (৳)", "0", validate: s => Dec(s, out var p) && p >= 0 ? null : "Enter a valid price");
        if (price is null) return;
        var group = dialogs.Prompt("New add-on", "Group (optional, e.g. Extras, Sauces)", "Extras");
        if (group is null) return;
        var r = await menu.SaveAddonAsync(new AddonInput(null, name, group, decimal.Parse(price, CultureInfo.InvariantCulture)));
        Show(r.Succeeded ? $"Add-on {name} created — tick it for this item and press Save add-ons" : r.Error, !r.Succeeded);
        if (r.Succeeded) await ReloadAsync(EditUuid);
    }

    // ------------------------------------------------------------------ CSV

    [RelayCommand]
    private async Task ExportAsync()
    {
        var dlg = new SaveFileDialog { Filter = "CSV (Excel) files|*.csv", FileName = $"menu-{DateTime.Now:yyyyMMdd}.csv" };
        if (dlg.ShowDialog() != true) return;
        await File.WriteAllTextAsync(dlg.FileName, await menu.ExportCsvAsync(), new System.Text.UTF8Encoding(false));
        Show($"Menu exported to {dlg.FileName}");
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dlg = new OpenFileDialog { Filter = "CSV files|*.csv", Title = $"Import menu — columns: {string.Join(", ", MenuService.CsvColumns)}" };
        if (dlg.ShowDialog() != true) return;
        string text;
        try { text = await File.ReadAllTextAsync(dlg.FileName); }
        catch (IOException ex) { Show($"Cannot read the file: {ex.Message}", true); return; }
        var r = await menu.ImportCsvAsync(text);
        if (!r.Succeeded) { dialogs.Error(r.Error!); return; }
        Show($"Import complete: {r.Value!.Created} created, {r.Value.Updated} updated");
        await ReloadAsync(null);
    }
}
