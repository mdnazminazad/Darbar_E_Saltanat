using System.Text.Json;
using DarbarESaltanat.Application.Sync;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Infrastructure;
using DarbarESaltanat.Infrastructure.Persistence;
using DarbarESaltanat.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarbarESaltanat.Tests;

/// <summary>Scriptable stand-in for the server.</summary>
public sealed class FakeSyncApi : ISyncApi
{
    public List<PushRequest> Pushes { get; } = [];
    public Func<PushChange, PushResult> Respond { get; set; } = c => new PushResult(c.Entity, c.Uuid, "SYNCED", c.SyncVersion + 1);
    public Queue<PullResponse> Pulls { get; } = new();
    public Exception? Fail { get; set; }

    public Task<PushResponse> PushAsync(SyncEndpoint ep, PushRequest r, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        Pushes.Add(r);
        return Task.FromResult(new PushResponse(r.BatchId, DateTime.UtcNow.ToString("O"), r.Changes.Select(Respond).ToList()));
    }
    public Task<PullResponse> PullAsync(SyncEndpoint ep, string? since, string? cursor, int limit, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        return Task.FromResult(Pulls.Count > 0 ? Pulls.Dequeue() : new PullResponse(null, false, null, "2026-11-20T10:00:00.000Z", []));
    }
    public Task AcknowledgeAsync(SyncEndpoint ep, string p, int n, CancellationToken ct = default) => Task.CompletedTask;
    public Task HeartbeatAsync(SyncEndpoint ep, int n, string v, CancellationToken ct = default) => Task.CompletedTask;
    public Task<DeviceInfo> GetDeviceAsync(SyncEndpoint ep, CancellationToken ct = default) => Task.FromResult(new DeviceInfo(ep.DeviceId, "Test", "ACTIVE", TestDb.SeedBranch, "Main", null));
}

/// <summary>Plain-text "protector" so the test can store a token without DPAPI.</summary>
internal sealed class PlainProtector : DarbarESaltanat.Application.Abstractions.ISecretProtector
{
    public string Protect(string plain) => plain;
    public string? Unprotect(string? value) => value;
}

/// <summary>SyncEngine + the real SqliteSyncStore on a real SQLite FILE (the store opens its own connections).</summary>
public sealed class SyncEngineTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"darbar-sync-{Guid.NewGuid():N}.db");
    private IAppDbContextFactory _factory = default!;
    private SqliteSyncStore _store = default!;
    private readonly FakeSyncApi _api = new();
    private readonly SyncStatus _status = new();
    private SyncEngine _engine = default!;
    private readonly FakeClock _clock = new() { UtcNow = DateTime.UtcNow };

    public async Task InitializeAsync()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();
        await using (var c = new SqliteConnection(cs)) await new DatabaseInitializer(NullLogger<DatabaseInitializer>.Instance).InitializeAsync(c);
        var device = new DeviceContext();
        device.Set("11111111-2222-3333-4444-555555555555", TestDb.SeedBranch);
        var gate = new SqliteWriteGate();
        _factory = new AppDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(cs).Options, _clock, device, gate);
        var state = new DarbarESaltanat.Infrastructure.Repositories.SyncStateStore(_factory);
        await state.SetAsync(SyncStateKeys.ApiBaseUrl, "https://example.test/api/v1");
        await state.SetAsync(SyncStateKeys.DeviceTokenProtected, "token");
        _store = new SqliteSyncStore(new SqliteConnectionString(cs), gate, new PlainProtector());
        _engine = new SyncEngine(_api, _store, _clock, _status, NullLogger<SyncEngine>.Instance);
    }

    public Task DisposeAsync() { SqliteConnection.ClearAllPools(); try { File.Delete(_path); } catch (IOException) { } return Task.CompletedTask; }

    private async Task<Setting> EditVatAsync(string value)
    {
        await using var db = _factory.Create();
        var s = await db.Settings.SingleAsync(x => x.SettingKey == "vat_rate");
        s.SettingValue = value;
        await db.SaveChangesAsync();
        return s;
    }
    private async Task<T> RowAsync<T>(string uuid) where T : DarbarESaltanat.Domain.Common.SyncEntity
    {
        await using var db = _factory.Create();
        return await db.Set<T>().IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Uuid == uuid);
    }

    private static PullResponse Pull(string entity, object row) =>
        new(null, false, null, "2026-11-20T10:00:00.000Z", [new PullEntity(entity, [JsonSerializer.SerializeToElement(row)])]);

    [Fact]
    public async Task Pushes_outbox_with_base_version_and_marks_rows_synced()
    {
        var s = await EditVatAsync("7.5");
        var r = await _engine.RunOnceAsync();
        r.Outcome.Should().Be(SyncOutcome.Completed);
        var change = _api.Pushes.Single().Changes.Single();
        change.Entity.Should().Be("settings");
        change.Operation.Should().Be("UPDATE");
        change.SyncVersion.Should().Be(1, "the seeded row is server version 1");
        change.Data.GetProperty("setting_value").GetString().Should().Be("7.5");
        change.Data.TryGetProperty("id", out _).Should().BeFalse();
        var row = await RowAsync<Setting>(s.Uuid);
        row.SyncVersion.Should().Be(2);
        row.SyncStatus.Should().Be(RowSyncStatus.Synced);
        (await _store.CountsAsync()).Unsynced.Should().Be(0);
        _status.Health.Should().Be(SyncHealth.Synced);
    }

    [Fact]
    public async Task Offline_keeps_the_batch_and_resends_the_same_batch_id()
    {
        await EditVatAsync("6");
        _api.Fail = new SyncOfflineException("no network");
        (await _engine.RunOnceAsync()).Outcome.Should().Be(SyncOutcome.Offline);
        _status.Health.Should().Be(SyncHealth.Offline);
        (await _store.CountsAsync()).Syncing.Should().Be(1);

        await EditVatAsync("6.5"); // a new edit while the batch is in flight gets its own entry
        _api.Fail = null;
        (await _engine.RunOnceAsync()).Outcome.Should().Be(SyncOutcome.Completed);
        _api.Pushes.Should().HaveCount(2);
        _api.Pushes[0].Changes.Single().Data.GetProperty("setting_value").GetString().Should().Be("6");
        _api.Pushes[1].Changes.Single().SyncVersion.Should().Be(2, "it continues from the version the first push produced");
        (await _store.RecentLogAsync(10)).Should().Contain(l => l.Message.Contains("resumed batch"));
    }

    [Fact]
    public async Task Conflict_freezes_the_row_until_the_server_publishes_a_resolution()
    {
        var s = await EditVatAsync("9");
        _api.Respond = c => new PushResult(c.Entity, c.Uuid, "CONFLICT", 2, "Server record changed after local update");
        var server = new { uuid = s.Uuid, branch_uuid = (string?)null, setting_key = "vat_rate", setting_value = "5", value_type = "NUMBER", group_name = "tax",
            created_at = "2026-01-01T00:00:00.000Z", updated_at = "2026-11-20T09:00:00.000Z", deleted_at = (string?)null, sync_version = 2, device_id = (string?)null };
        _api.Pulls.Enqueue(Pull("settings", server));
        var r = await _engine.RunOnceAsync();
        r.Conflicts.Should().Be(1);
        var frozen = await RowAsync<Setting>(s.Uuid);
        frozen.SettingValue.Should().Be("9", "the till keeps its copy while the conflict is open");
        frozen.SyncStatus.Should().Be(RowSyncStatus.Conflict);
        _status.Health.Should().Be(SyncHealth.Conflicts);

        _api.Pulls.Enqueue(Pull("settings", server with { setting_value = "5.5", sync_version = 3 })); // admin resolved on the server
        await _engine.RunOnceAsync();
        var resolved = await RowAsync<Setting>(s.Uuid);
        resolved.SettingValue.Should().Be("5.5");
        resolved.SyncVersion.Should().Be(3);
        resolved.SyncStatus.Should().Be(RowSyncStatus.Synced);
        (await _store.CountsAsync()).Conflicts.Should().Be(0);
    }

    [Fact]
    public async Task Pull_inserts_new_rows_converts_types_and_never_overwrites_unsynced_local_edits()
    {
        var item = new
        {
            uuid = Guid.NewGuid().ToString(), branch_uuid = TestDb.SeedBranch, category_uuid = (await FirstCategoryAsync()), name = "Mango Lassi", name_bn = (string?)null,
            description = (string?)null, sku = "BEV-099", barcode = (string?)null, image_url = (string?)null, cost_price = "40.00", selling_price = "120.00", tax_rate = "0.00",
            prep_time_minutes = 5, sort_order = 9, is_available = true, is_active = false,
            created_at = "2026-11-20T09:00:00.000Z", updated_at = "2026-11-20T09:00:00.000Z", deleted_at = (string?)null, sync_version = 1, device_id = (string?)null,
        };
        _api.Pulls.Enqueue(Pull("menu_items", item));
        (await _engine.RunOnceAsync()).Pulled.Should().Be(1);
        var row = await RowAsync<MenuItem>(item.uuid);
        row.SellingPrice.Should().Be(120m);
        row.IsAvailable.Should().BeTrue();
        row.IsActive.Should().BeFalse();
        row.SyncStatus.Should().Be(RowSyncStatus.Synced);
        (await _store.CountsAsync()).Pending.Should().Be(0, "applying server rows must not queue them back");

        _api.Fail = new SyncOfflineException("offline"); // keep the local edit unsynced
        var s = await EditVatAsync("8");
        await _engine.RunOnceAsync();
        _api.Fail = null;
        _api.Respond = c => new PushResult(c.Entity, c.Uuid, "FAILED", null, "test keeps it unsynced");
        await _store.RetryFailedAsync(DateTime.UtcNow);
        _api.Pulls.Enqueue(Pull("settings", new { uuid = s.Uuid, setting_key = "vat_rate", setting_value = "99", value_type = "NUMBER", group_name = "tax",
            created_at = "2026-01-01T00:00:00.000Z", updated_at = "2026-11-20T09:00:00.000Z", sync_version = 7 }));
        await _engine.RunOnceAsync();
        (await RowAsync<Setting>(s.Uuid)).SettingValue.Should().Be("8");
    }

    private async Task<string> FirstCategoryAsync()
    {
        await using var db = _factory.Create();
        return (await db.Set<Category>().FirstAsync()).Uuid;
    }
}
