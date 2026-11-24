using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Security;

namespace DarbarESaltanat.Application.Auth;

public sealed record UserSession(
    string UserUuid, string Username, string FullName, bool MustChangePassword,
    IReadOnlyList<string> Roles, IReadOnlySet<string> Permissions, DateTime SignedInAtUtc)
{
    public bool IsSuperAdmin => Roles.Contains(RoleCodes.SuperAdmin);
    public bool Has(string permission) => IsSuperAdmin || Permissions.Contains(permission);
}

/// <summary>Holds the signed-in user; every sensitive service call goes through <see cref="Require"/>.</summary>
public interface ICurrentSession
{
    UserSession? Current { get; }
    bool IsLocked { get; }
    event EventHandler? Changed;
    void SignIn(UserSession session);
    void SignOut();
    void Lock();
    void Unlock();
    bool Has(string permission);
    /// <summary>Throws <see cref="PermissionDeniedException"/> when the permission is missing.</summary>
    UserSession Require(string permission);
}

public sealed class CurrentSession : ICurrentSession
{
    private readonly object _gate = new();
    public UserSession? Current { get; private set; }
    public bool IsLocked { get; private set; }
    public event EventHandler? Changed;

    public void SignIn(UserSession session) { lock (_gate) { Current = session; IsLocked = false; } Changed?.Invoke(this, EventArgs.Empty); }
    public void SignOut() { lock (_gate) { Current = null; IsLocked = false; } Changed?.Invoke(this, EventArgs.Empty); }
    public void Lock() { lock (_gate) { if (Current is null) return; IsLocked = true; } Changed?.Invoke(this, EventArgs.Empty); }
    public void Unlock() { lock (_gate) { IsLocked = false; } Changed?.Invoke(this, EventArgs.Empty); }
    public bool Has(string permission) => Current is { } s && !IsLocked && s.Has(permission);

    public UserSession Require(string permission)
    {
        var s = Current ?? throw new PermissionDeniedException(permission);
        if (IsLocked || !s.Has(permission)) throw new PermissionDeniedException(permission);
        return s;
    }
}
