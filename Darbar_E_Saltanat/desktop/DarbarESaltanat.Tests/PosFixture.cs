using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Customers;
using DarbarESaltanat.Application.Menu;
using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Application.Tables;
using DarbarESaltanat.Domain.Common;
using DarbarESaltanat.Domain.Security;
using DarbarESaltanat.Infrastructure.Persistence;
using DarbarESaltanat.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace DarbarESaltanat.Tests;

/// <summary>Real services over the seeded in-memory SQLite database.</summary>
public sealed class PosFixture : IAsyncDisposable
{
    public const string AdminUuid = "adfd06ed-d448-58be-a342-c814ee273873"; // seeded super admin
    public required TestDb Db { get; init; }
    public required CurrentSession Session { get; init; }
    public required OrderService Orders { get; init; }
    public required MenuService Menu { get; init; }
    public required TableService Tables { get; init; }
    public required CustomerService Customers { get; init; }

    public static readonly string[] CashierPermissions =
    [
        Permissions.OrdersView, Permissions.OrdersCreate, Permissions.OrdersTransfer, Permissions.OrdersReprint, Permissions.PaymentsTake,
        Permissions.DiscountsApply, Permissions.MenuView, Permissions.TablesManage, Permissions.CustomersView, Permissions.CustomersManage,
    ];

    /// <param name="permissions">null = Super Admin; otherwise exactly these permissions.</param>
    public static async Task<PosFixture> CreateAsync(string[]? permissions = null)
    {
        var t = await TestDb.CreateAsync();
        var session = new CurrentSession();
        session.SignIn(new UserSession(AdminUuid, "admin", "System Administrator", false,
            permissions is null ? new[] { "SUPER_ADMIN" } : new[] { "CASHIER" }, (permissions ?? Array.Empty<string>()).ToHashSet(), t.Clock.UtcNow));
        var uow = new EfUnitOfWorkFactory(t.Factory);
        var audit = new AuditFactory(session, t.Device, t.Clock);
        var settings = new SettingsService(new SettingsRepository(t.Factory), session, t.Device, t.Clock);
        return new PosFixture
        {
            Db = t, Session = session,
            Orders = new OrderService(uow, session, t.Clock, t.Device, settings, audit),
            Menu = new MenuService(uow, session, audit),
            Tables = new TableService(uow, session, audit),
            Customers = new CustomerService(uow, session, audit),
        };
    }

    public async Task<T> FindAsync<T>(Func<T, bool> match) where T : SyncEntity
    {
        await using var db = Db.Db();
        return (await db.Set<T>().AsNoTracking().ToListAsync()).Single(match);
    }

    public async Task<List<T>> AllAsync<T>(bool includeDeleted = false) where T : SyncEntity
    {
        await using var db = Db.Db();
        var q = includeDeleted ? db.Set<T>().IgnoreQueryFilters() : db.Set<T>();
        return await q.AsNoTracking().ToListAsync();
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

internal static class ResultExtensions
{
    public static T Expect<T>(this Result<T> r) => r.Succeeded ? r.Value! : throw new Xunit.Sdk.XunitException($"Expected success but got: {r.Error}");
}
