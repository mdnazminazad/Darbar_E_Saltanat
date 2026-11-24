using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DarbarESaltanat.Infrastructure.Repositories;

public sealed class UserRepository(IAppDbContextFactory factory) : IUserRepository
{
    public async Task<User?> FindByLoginAsync(string login, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var l = login.ToLower();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username.ToLower() == l || (u.Email != null && u.Email.ToLower() == l), ct);
    }

    public async Task<User?> FindByUuidAsync(string uuid, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Uuid == uuid, ct);
    }

    public async Task<IReadOnlyList<User>> ListPinUsersAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return await db.Users.AsNoTracking().Where(u => u.IsActive && u.PinHash != null).OrderBy(u => u.FullName).ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions)> GetAccessAsync(string userUuid, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleUuid equals r.Uuid
                           where ur.UserUuid == userUuid select r).AsNoTracking().ToListAsync(ct);
        var roleUuids = roles.Select(r => r.Uuid).ToList();
        var perms = await (from rp in db.RolePermissions join p in db.Permissions on rp.PermissionUuid equals p.Uuid
                           where roleUuids.Contains(rp.RoleUuid) select p.Code).Distinct().ToListAsync(ct);
        return (roles.Select(r => r.Code).ToList(), perms);
    }

    /// <summary>Local-only bookkeeping via ExecuteUpdate: bypasses the change tracker, so nothing is queued for sync.</summary>
    public async Task SaveLoginStateAsync(User user, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        using var _ = await db.AcquireWriteLockAsync(ct);
        await db.Users.Where(u => u.Uuid == user.Uuid).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FailedLoginCount, user.FailedLoginCount)
            .SetProperty(u => u.LockedUntil, user.LockedUntil)
            .SetProperty(u => u.LastLoginAt, user.LastLoginAt), ct);
    }
}

public sealed class SettingsRepository(IAppDbContextFactory factory) : ISettingsRepository
{
    public async Task<IReadOnlyList<Setting>> GetEffectiveAsync(string? branchUuid, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var rows = await db.Settings.AsNoTracking().Where(s => s.BranchUuid == null || s.BranchUuid == branchUuid).ToListAsync(ct);
        // Branch rows override chain-wide defaults.
        return rows.GroupBy(r => r.SettingKey).Select(g => g.FirstOrDefault(x => x.BranchUuid != null) ?? g.First()).ToList();
    }

    public async Task UpsertAsync(string? branchUuid, IReadOnlyDictionary<string, (string? Value, string Type)> values, AuditLog audit, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var keys = values.Keys.ToList();
        var existing = await db.Settings.IgnoreQueryFilters().Where(s => s.BranchUuid == branchUuid && keys.Contains(s.SettingKey)).ToListAsync(ct);
        foreach (var (key, (value, type)) in values)
        {
            var row = existing.FirstOrDefault(s => s.SettingKey == key);
            if (row is null) db.Settings.Add(new Setting { SettingKey = key, SettingValue = value, ValueType = type, BranchUuid = branchUuid });
            else if (row.SettingValue != value || row.DeletedAt != null || row.ValueType != type)
            { row.SettingValue = value; row.ValueType = type; row.DeletedAt = null; }
        }
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct); // settings + audit + outbox rows: one transaction
    }
}

public sealed class AuditRepository(IAppDbContextFactory factory) : IAuditRepository
{
    public async Task AddAsync(AuditLog entry, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class SyncStateStore(IAppDbContextFactory factory) : ISyncStateStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return (await db.SyncState.AsNoTracking().FirstOrDefaultAsync(s => s.StateKey == key, ct))?.StateValue;
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var row = await db.SyncState.FirstOrDefaultAsync(s => s.StateKey == key, ct);
        if (row is null) db.SyncState.Add(new SyncStateEntry { StateKey = key, StateValue = value, UpdatedAt = DateTime.UtcNow });
        else { row.StateValue = value; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> CountOutboxAsync(string status, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return await db.SyncOutbox.CountAsync(o => o.Status == status, ct);
    }
}
