using System.Text.Json;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DarbarESaltanat.Tests;

public class SyncOutboxTests
{
    [Fact]
    public async Task Creating_a_row_writes_business_row_and_outbox_entry_together()
    {
        await using var t = await TestDb.CreateAsync();
        await using (var db = t.Db())
        {
            db.Settings.Add(new Setting { SettingKey = "test_key", SettingValue = "42", ValueType = "NUMBER" });
            await db.SaveChangesAsync();
        }
        await using var check = t.Db();
        var row = await check.Settings.SingleAsync(s => s.SettingKey == "test_key");
        row.SyncStatus.Should().Be(RowSyncStatus.Pending);
        row.DeviceId.Should().Be(t.Device.DeviceId);
        row.CreatedAt.Should().Be(t.Clock.UtcNow);

        var outbox = await check.SyncOutbox.SingleAsync();
        outbox.Operation.Should().Be(SyncOperation.Create);
        outbox.EntityType.Should().Be("settings");
        outbox.EntityUuid.Should().Be(row.Uuid);
        outbox.BaseVersion.Should().Be(0);
        var payload = JsonDocument.Parse(outbox.PayloadJson).RootElement;
        payload.GetProperty("setting_key").GetString().Should().Be("test_key");
        payload.GetProperty("updated_at").GetString().Should().Be("2026-11-20T10:00:00.000Z");
        payload.TryGetProperty("id", out _).Should().BeFalse("local ids never leave the device");
    }

    [Fact]
    public async Task Repeated_edits_before_sync_are_coalesced_into_one_entry()
    {
        await using var t = await TestDb.CreateAsync();
        await using var db = t.Db();
        var s = new Setting { SettingKey = "k", SettingValue = "1" };
        db.Settings.Add(s); await db.SaveChangesAsync();
        s.SettingValue = "2"; await db.SaveChangesAsync();
        s.SettingValue = "3"; await db.SaveChangesAsync();

        var outbox = await db.SyncOutbox.ToListAsync();
        outbox.Should().ContainSingle();
        outbox[0].Operation.Should().Be(SyncOperation.Create);
        JsonDocument.Parse(outbox[0].PayloadJson).RootElement.GetProperty("setting_value").GetString().Should().Be("3");
    }

    [Fact]
    public async Task Update_of_a_synced_row_queues_UPDATE_with_server_base_version()
    {
        await using var t = await TestDb.CreateAsync();
        await using var db = t.Db();
        var vat = await db.Settings.SingleAsync(s => s.SettingKey == "vat_rate"); // seeded as SYNCED v1
        vat.SyncVersion.Should().Be(1);
        vat.SettingValue = "7.5";
        await db.SaveChangesAsync();

        var o = await db.SyncOutbox.SingleAsync();
        o.Operation.Should().Be(SyncOperation.Update);
        o.BaseVersion.Should().Be(1);
    }

    [Fact]
    public async Task Delete_becomes_soft_delete_and_is_queued()
    {
        await using var t = await TestDb.CreateAsync();
        await using (var db = t.Db())
        {
            var row = await db.Settings.SingleAsync(s => s.SettingKey == "theme");
            db.Settings.Remove(row);
            await db.SaveChangesAsync();
        }
        await using var check = t.Db();
        (await check.Settings.AnyAsync(s => s.SettingKey == "theme")).Should().BeFalse("soft-deleted rows are filtered out");
        var deleted = await check.Settings.IgnoreQueryFilters().SingleAsync(s => s.SettingKey == "theme");
        deleted.DeletedAt.Should().NotBeNull();
        (await check.SyncOutbox.SingleAsync()).Operation.Should().Be(SyncOperation.Delete);
    }

    [Fact]
    public async Task Failed_save_rolls_back_both_business_row_and_outbox()
    {
        await using var t = await TestDb.CreateAsync();
        await using (var db = t.Db())
        {
            db.Settings.Add(new Setting { SettingKey = "will_rollback" });
            db.UserRoles.Add(new UserRole { UserUuid = Guid.NewGuid().ToString(), RoleUuid = Guid.NewGuid().ToString() }); // FK violation
            var act = () => db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
        await using var check = t.Db();
        (await check.Settings.AnyAsync(s => s.SettingKey == "will_rollback")).Should().BeFalse();
        (await check.SyncOutbox.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Changes_applied_from_the_server_are_not_requeued()
    {
        await using var t = await TestDb.CreateAsync();
        await using var db = t.Db();
        db.ApplyingRemoteChanges = true;
        db.Settings.Add(new Setting { SettingKey = "from_server", SyncVersion = 4, CreatedAt = t.Clock.UtcNow, UpdatedAt = t.Clock.UtcNow });
        await db.SaveChangesAsync();
        (await db.SyncOutbox.CountAsync()).Should().Be(0);
        (await db.Settings.SingleAsync(s => s.SettingKey == "from_server")).SyncStatus.Should().Be(RowSyncStatus.Synced);
    }

    [Fact]
    public async Task Append_only_records_cannot_be_edited()
    {
        await using var t = await TestDb.CreateAsync();
        await using var db = t.Db();
        var log = new AuditLog { Action = "x", OccurredAt = t.Clock.UtcNow };
        db.AuditLogs.Add(log); await db.SaveChangesAsync();
        log.Action = "tampered";
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
    }

    [Fact]
    public async Task Chain_wide_settings_keep_a_null_branch()
    {
        await using var t = await TestDb.CreateAsync();
        await using var db = t.Db();
        db.Settings.Add(new Setting { SettingKey = "global_one" });
        await db.SaveChangesAsync();
        (await db.Settings.SingleAsync(s => s.SettingKey == "global_one")).BranchUuid.Should().BeNull();
    }
}
