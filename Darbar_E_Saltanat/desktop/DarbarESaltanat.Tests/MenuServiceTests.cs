using DarbarESaltanat.Application.Customers;
using DarbarESaltanat.Application.Menu;
using DarbarESaltanat.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace DarbarESaltanat.Tests;

public class MenuServiceTests
{
    [Fact]
    public async Task Csv_import_updates_by_sku_creates_new_items_and_categories()
    {
        await using var f = await PosFixture.CreateAsync();
        var csv = "sku,name,category,selling_price,cost_price,is_available\n" +
                  "DES-001,Firni,Desserts,95,35,yes\n" +
                  "NEW-001,\"Mango Lassi, Large\",Drinks,\"1,200\",40,no\n";
        var report = (await f.Menu.ImportCsvAsync(csv)).Expect();
        report.Created.Should().Be(1);
        report.Updated.Should().Be(1);
        (await f.FindAsync<MenuItem>(i => i.Sku == "DES-001")).SellingPrice.Should().Be(95m);
        var lassi = await f.FindAsync<MenuItem>(i => i.Sku == "NEW-001");
        lassi.Name.Should().Be("Mango Lassi, Large");
        lassi.SellingPrice.Should().Be(1200m);
        lassi.IsAvailable.Should().BeFalse();
        (await f.FindAsync<Category>(c => c.Uuid == lassi.CategoryUuid)).Name.Should().Be("Drinks");
    }

    [Fact]
    public async Task Csv_import_is_all_or_nothing()
    {
        await using var f = await PosFixture.CreateAsync();
        var r = await f.Menu.ImportCsvAsync("sku,name,category,selling_price\nX-1,Good Item,Sides,50\nX-2,Bad Item,Sides,abc\n");
        r.Succeeded.Should().BeFalse();
        r.Error.Should().Contain("Row 3");
        (await f.AllAsync<MenuItem>()).Should().NotContain(i => i.Sku == "X-1");
    }

    [Fact]
    public async Task Csv_export_round_trips_and_escapes()
    {
        await using var f = await PosFixture.CreateAsync();
        var csv = await f.Menu.ExportCsvAsync();
        var rows = Csv.Parse(csv);
        rows[0].Should().Equal(MenuService.CsvColumns);
        rows.Should().Contain(r => r[1] == "Kacchi Biryani" && r[4] == "450.00");
        Csv.Escape("=SUM(A1)").Should().StartWith("'");
        Csv.Escape("a,b").Should().Be("\"a,b\"");
    }

    [Fact]
    public async Task Item_validation_and_unique_sku()
    {
        await using var f = await PosFixture.CreateAsync();
        var cat = (await f.FindAsync<Category>(c => c.Name == "Sides")).Uuid;
        MenuItemInput Input(string name, decimal price, string? sku) => new(null, cat, name, null, null, sku, null, 0, price, 0, 10, 0, true, true);
        (await f.Menu.SaveItemAsync(Input("X", 10, null))).ErrorCode.Should().Be("VALIDATION");
        (await f.Menu.SaveItemAsync(Input("Papad", -1, null))).ErrorCode.Should().Be("VALIDATION");
        (await f.Menu.SaveItemAsync(Input("Papad", 20, "SID-001"))).Error.Should().Contain("SKU");
        (await f.Menu.SaveItemAsync(Input("Papad", 20, "SID-009"))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Variants_allow_one_default_and_deleting_a_category_with_items_is_blocked()
    {
        await using var f = await PosFixture.CreateAsync();
        var tehari = (await f.FindAsync<MenuItem>(i => i.Name == "Beef Tehari")).Uuid;
        (await f.Menu.SaveVariantsAsync(tehari, [new VariantInput(null, "Half", 320, 0, true), new VariantInput(null, "Full", 600, 0, true)])).Succeeded.Should().BeFalse();
        (await f.Menu.SaveVariantsAsync(tehari, [new VariantInput(null, "Half", 320, 0, false), new VariantInput(null, "Full", 600, 0, false)])).Succeeded.Should().BeTrue();
        var variants = (await f.AllAsync<ItemVariant>()).Where(v => v.MenuItemUuid == tehari).ToList();
        variants.Should().HaveCount(2);
        variants.Single(v => v.IsDefault).Name.Should().Be("Half", "the first variant becomes default when none is chosen");
        var sides = (await f.FindAsync<Category>(c => c.Name == "Sides")).Uuid;
        (await f.Menu.DeleteCategoryAsync(sides)).Error.Should().Contain("still has");
    }

    [Theory]
    [InlineData("+880 1712-345678", "01712345678", true)]
    [InlineData("8801912345678", "01912345678", true)]
    [InlineData("01212345678", "01212345678", false)]
    [InlineData("12345", "12345", false)]
    public void Bangladeshi_phone_numbers_are_normalised(string raw, string normalised, bool valid)
    {
        CustomerService.NormalizePhone(raw).Should().Be(normalised);
        CustomerService.IsValidBdMobile(normalised).Should().Be(valid);
    }

    [Fact]
    public async Task Customers_are_created_once_per_phone()
    {
        await using var f = await PosFixture.CreateAsync();
        (await f.Customers.CreateAsync(new CustomerInput("Rahim Uddin", "01712345678"))).Succeeded.Should().BeTrue();
        (await f.Customers.CreateAsync(new CustomerInput("Someone", "+8801712345678"))).ErrorCode.Should().Be("DUPLICATE");
        (await f.Customers.FindByPhoneAsync("880-1712-345678"))!.Name.Should().Be("Rahim Uddin");
        (await f.Customers.SearchAsync("rahim")).Should().ContainSingle();
    }
}
