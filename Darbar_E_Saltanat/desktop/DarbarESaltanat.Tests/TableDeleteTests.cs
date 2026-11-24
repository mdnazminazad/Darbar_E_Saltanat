using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Application.Tables;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DarbarESaltanat.Tests;

public class TableDeleteTests
{
    private static async Task<string> TableAsync(PosFixture f, string name) => (await f.FindAsync<RestaurantTable>(t => t.Name == name)).Uuid;

    [Fact]
    public async Task Delete_removes_a_free_table_and_queues_a_sync_delete()
    {
        await using var f = await PosFixture.CreateAsync();
        var t9 = await TableAsync(f, "T9");

        (await f.Tables.DeleteAsync(t9)).Succeeded.Should().BeTrue();

        (await f.AllAsync<RestaurantTable>()).Should().NotContain(t => t.Uuid == t9, "deleted tables vanish from the floor plan");
        (await f.Tables.GetBoardAsync()).Should().NotContain(t => t.Table.Uuid == t9);
        var row = (await f.AllAsync<RestaurantTable>(includeDeleted: true)).Single(t => t.Uuid == t9);
        row.DeletedAt.Should().NotBeNull();
        await using var db = f.Db.Db();
        (await db.SyncOutbox.ToListAsync()).Should().Contain(o => o.EntityUuid == t9 && o.Operation == SyncOperation.Delete);
    }

    [Fact]
    public async Task A_deleted_table_name_can_be_used_again()
    {
        await using var f = await PosFixture.CreateAsync();
        (await f.Tables.DeleteAsync(await TableAsync(f, "T9"))).Succeeded.Should().BeTrue();

        var again = await f.Tables.SaveAsync(new TableInput(null, "T9", 6, "Main Hall"));

        again.Succeeded.Should().BeTrue("the unique (branch, name) index must not be blocked by the deleted row");
        (await f.Tables.GetBoardAsync()).Count(t => t.Table.Name == "T9").Should().Be(1);
    }

    [Fact]
    public async Task Delete_is_refused_while_the_table_has_an_open_order()
    {
        await using var f = await PosFixture.CreateAsync();
        var t8 = await TableAsync(f, "T8");
        (await f.Orders.CreateAsync(new CreateOrderRequest(OrderType.DineIn, t8, GuestCount: 2))).Expect();

        var r = await f.Tables.DeleteAsync(t8);

        r.Succeeded.Should().BeFalse();
        r.ErrorCode.Should().Be("TABLE_BUSY");
        (await f.AllAsync<RestaurantTable>()).Should().Contain(t => t.Uuid == t8);
    }

    [Fact]
    public async Task Edit_renames_a_table_and_changes_seats()
    {
        await using var f = await PosFixture.CreateAsync();
        var t7 = await TableAsync(f, "T7");

        var r = await f.Tables.SaveAsync(new TableInput(t7, "VIP-1", 12, "VIP Room"));

        r.Succeeded.Should().BeTrue();
        var row = await f.FindAsync<RestaurantTable>(t => t.Uuid == t7);
        row.Name.Should().Be("VIP-1");
        row.Capacity.Should().Be(12);
        row.Section.Should().Be("VIP Room");
    }
}
