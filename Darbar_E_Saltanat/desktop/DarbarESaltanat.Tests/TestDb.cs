using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DarbarESaltanat.Tests;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
}

/// <summary>Real SQLite (in-memory, shared connection) initialised from the same embedded schema + seed as production.</summary>
public sealed class TestDb : IAsyncDisposable
{
    public const string SeedBranch = "f7cde179-b1c6-5c34-8b73-e487abb90516";
    public SqliteConnection Connection { get; } = new("Data Source=:memory:");
    public FakeClock Clock { get; } = new();
    public DeviceContext Device { get; } = new();
    public IAppDbContextFactory Factory { get; private set; } = default!;

    public static async Task<TestDb> CreateAsync()
    {
        var t = new TestDb();
        await t.Connection.OpenAsync();
        await new DatabaseInitializer(NullLogger<DatabaseInitializer>.Instance).InitializeAsync(t.Connection);
        t.Device.Set("11111111-2222-3333-4444-555555555555", SeedBranch);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(t.Connection).Options;
        t.Factory = new AppDbContextFactory(options, t.Clock, t.Device, new SqliteWriteGate());
        return t;
    }

    public AppDbContext Db() => Factory.Create();
    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
