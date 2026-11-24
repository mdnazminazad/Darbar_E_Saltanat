using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Domain.Security;
using FluentValidation;

namespace DarbarESaltanat.Application.Tables;

public sealed record TableOrderInfo(string OrderUuid, string OrderNo, string Status, decimal Total, int GuestCount, DateTime OpenedAtUtc);
public sealed record TableTile(RestaurantTable Table, IReadOnlyList<TableOrderInfo> Orders)
{
    public TableOrderInfo? Primary => Orders.OrderBy(o => o.OpenedAtUtc).FirstOrDefault();
}

public sealed record TableInput(string? Uuid, string Name, int Capacity, string? Section, bool IsActive = true);

public sealed class TableInputValidator : AbstractValidator<TableInput>
{
    public TableInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a table name").MaximumLength(30);
        RuleFor(x => x.Capacity).InclusiveBetween(1, 100).WithMessage("Capacity must be 1–100");
        RuleFor(x => x.Section).MaximumLength(60);
    }
}

public sealed class TableService(IUnitOfWorkFactory uowFactory, ICurrentSession session, AuditFactory audit)
{
    private readonly TableInputValidator _validator = new();

    /// <summary>Floor plan: every active table with the open orders on it (including merged tables).</summary>
    public async Task<IReadOnlyList<TableTile>> GetBoardAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersView);
        var active = OrderStatus.Active.ToHashSet();
        await using var uow = uowFactory.Begin();
        var tables = await uow.ListAsync<RestaurantTable>(t => t.IsActive, ct);
        var allOrdersWithTable = await uow.ListAsync<Order>(o => o.TableUuid != null, ct);
        var orders = allOrdersWithTable.Where(o => active.Contains(o.Status)).ToList();
        var orderIds = orders.Select(o => o.Uuid).ToList();
        var links = orderIds.Count == 0 ? [] : await uow.ListAsync<OrderTable>(l => orderIds.Contains(l.OrderUuid), ct);
        TableOrderInfo Info(Order o) => new(o.Uuid, o.OrderNo, o.Status, o.Total, o.GuestCount, o.OrderDate);
        return tables
            .OrderBy(t => t.Section).ThenBy(t => t.Name.Length).ThenBy(t => t.Name)
            .Select(t => new TableTile(t, orders.Where(o => o.TableUuid == t.Uuid || links.Any(l => l.OrderUuid == o.Uuid && l.TableUuid == t.Uuid))
                .Select(Info).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<RestaurantTable>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        session.Require(Permissions.OrdersView);
        await using var uow = uowFactory.Begin();
        var rows = await uow.ListAsync<RestaurantTable>(t => includeInactive || t.IsActive, ct);
        return rows.OrderBy(t => t.Section).ThenBy(t => t.Name.Length).ThenBy(t => t.Name).ToList();
    }

    /// <summary>Manual status changes: Available ⇄ Reserved/Cleaning. Occupied/Billing are driven by orders.</summary>
    public async Task<Result> SetStatusAsync(string tableUuid, string status, CancellationToken ct = default)
    {
        session.Require(Permissions.TablesManage);
        if (status is not (TableStatus.Available or TableStatus.Reserved or TableStatus.Cleaning))
            return Result.Fail("Occupied and Billing are set automatically by orders");
        var active = OrderStatus.Active.ToHashSet();
        await using var uow = uowFactory.Begin();
        var t = await uow.FindAsync<RestaurantTable>(tableUuid, ct);
        if (t is null) return Result.Fail("Table not found");
        var orders = await uow.ListAsync<Order>(o => o.TableUuid == tableUuid, ct: ct);
        var busy = orders.Count(o => active.Contains(o.Status));
        if (busy > 0) return Result.Fail($"Table {t.Name} has an open order");
        if (t.Status == status) return Result.Ok();
        var old = t.Status;
        t.Status = status;
        uow.Add(audit.Create("restaurant_tables.status", "restaurant_tables", t.Uuid, new { status = old }, new { status }));
        await uow.CommitAsync(ct);
        return Result.Ok();
    }

    public async Task<Result<RestaurantTable>> SaveAsync(TableInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.TablesManage);
        var v = _validator.Validate(input);
        if (!v.IsValid) return Result<RestaurantTable>.Fail(v.Errors[0].ErrorMessage, "VALIDATION");
        var name = input.Name.Trim();
        await using var uow = uowFactory.Begin();
        var clash = await uow.ListAsync<RestaurantTable>(t => t.Name == name && t.Uuid != input.Uuid, ct);
        if (clash.Count > 0) return Result<RestaurantTable>.Fail($"Table {name} already exists", "DUPLICATE");
        RestaurantTable table;
        if (input.Uuid is null) { table = new RestaurantTable(); uow.Add(table); }
        else
        {
            table = await uow.FindAsync<RestaurantTable>(input.Uuid, ct) ?? throw new InvalidOperationException("Table not found");
            if (!input.IsActive && table.Status is TableStatus.Occupied or TableStatus.Billing)
                return Result<RestaurantTable>.Fail("Close the table's order before deactivating it");
        }
        table.Name = name; table.Capacity = input.Capacity; table.Section = string.IsNullOrWhiteSpace(input.Section) ? null : input.Section.Trim(); table.IsActive = input.IsActive;
        uow.Add(audit.Create(input.Uuid is null ? "restaurant_tables.create" : "restaurant_tables.update", "restaurant_tables", table.Uuid, null, input));
        await uow.CommitAsync(ct);
        return Result<RestaurantTable>.Ok(table);
    }
}
