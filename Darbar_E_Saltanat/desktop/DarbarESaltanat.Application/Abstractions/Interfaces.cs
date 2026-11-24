using DarbarESaltanat.Domain.Entities;

namespace DarbarESaltanat.Application.Abstractions;

public interface IClock { DateTime UtcNow { get; } }

public interface IPasswordHasher
{
    string Hash(string secret);
    bool Verify(string secret, string? hash);
}

/// <summary>Identity of this desktop installation.</summary>
public interface IDeviceContext
{
    string DeviceId { get; }
    string BranchUuid { get; }
}

public interface IUserRepository
{
    Task<User?> FindByLoginAsync(string usernameOrEmail, CancellationToken ct = default);
    Task<User?> FindByUuidAsync(string uuid, CancellationToken ct = default);
    /// <summary>Active users that have a PIN (for the cashier quick-login picker).</summary>
    Task<IReadOnlyList<User>> ListPinUsersAsync(CancellationToken ct = default);
    Task<(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions)> GetAccessAsync(string userUuid, CancellationToken ct = default);
    /// <summary>Persists login-attempt bookkeeping. Local-only: not queued for sync.</summary>
    Task SaveLoginStateAsync(User user, CancellationToken ct = default);
}

public interface ISettingsRepository
{
    Task<IReadOnlyList<Setting>> GetEffectiveAsync(string? branchUuid, CancellationToken ct = default);
    /// <summary>Upserts settings and queues them for sync in ONE SQLite transaction.</summary>
    Task UpsertAsync(string? branchUuid, IReadOnlyDictionary<string, (string? Value, string Type)> values, AuditLog audit, CancellationToken ct = default);
}

public interface IAuditRepository
{
    Task AddAsync(AuditLog entry, CancellationToken ct = default);
}

public interface ISyncStateStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, CancellationToken ct = default);
    Task<int> CountOutboxAsync(string status, CancellationToken ct = default);
}

/// <summary>Protects secrets at rest (Windows DPAPI on desktop).</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string? Unprotect(string? protectedValue);
}
