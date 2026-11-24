using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Entities;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace DarbarESaltanat.Application.Auth;

public sealed record LoginRequest(string UsernameOrEmail, string Password);
public sealed record PinLoginRequest(string UserUuid, string Pin);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.UsernameOrEmail).NotEmpty().WithMessage("Enter your username or email").MaximumLength(150);
        RuleFor(x => x.Password).NotEmpty().WithMessage("Enter your password").MaximumLength(128);
    }
}

public sealed class PinLoginRequestValidator : AbstractValidator<PinLoginRequest>
{
    public PinLoginRequestValidator()
    {
        RuleFor(x => x.UserUuid).NotEmpty().WithMessage("Choose a user");
        RuleFor(x => x.Pin).NotEmpty().Matches(@"^\d{4,6}$").WithMessage("PIN must be 4–6 digits");
    }
}

public sealed class AuthOptions
{
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

/// <summary>
/// Offline authentication against the local SQLite copy of users (bcrypt hashes are synced from the server).
/// Works with no internet connection.
/// </summary>
public sealed class AuthService(
    IUserRepository users, IPasswordHasher hasher, ICurrentSession session, IAuditRepository audit,
    IClock clock, IDeviceContext device, AuthOptions options, ILogger<AuthService> log)
{
    private const string Generic = "Invalid username or password";
    private readonly LoginRequestValidator _loginValidator = new();
    private readonly PinLoginRequestValidator _pinValidator = new();

    public async Task<Result<UserSession>> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var v = _loginValidator.Validate(req);
        if (!v.IsValid) return Result<UserSession>.Fail(v.Errors[0].ErrorMessage, "VALIDATION");

        var user = await users.FindByLoginAsync(req.UsernameOrEmail.Trim(), ct);
        if (user is null)
        {
            hasher.Verify(req.Password, null); // keep timing similar for unknown users
            return Result<UserSession>.Fail(Generic, "INVALID_CREDENTIALS");
        }
        return await CompleteAsync(user, hasher.Verify(req.Password, user.PasswordHash), "password", ct);
    }

    public async Task<Result<UserSession>> PinLoginAsync(PinLoginRequest req, CancellationToken ct = default)
    {
        var v = _pinValidator.Validate(req);
        if (!v.IsValid) return Result<UserSession>.Fail(v.Errors[0].ErrorMessage, "VALIDATION");
        var user = await users.FindByUuidAsync(req.UserUuid, ct);
        if (user is null || user.PinHash is null) return Result<UserSession>.Fail("PIN login is not set up for this user", "NO_PIN");
        return await CompleteAsync(user, hasher.Verify(req.Pin, user.PinHash), "pin", ct);
    }

    /// <summary>Re-authenticates the locked user (or lets a different user take over the terminal).</summary>
    public async Task<Result<UserSession>> UnlockAsync(string secret, CancellationToken ct = default)
    {
        var current = session.Current;
        if (current is null) return Result<UserSession>.Fail("No session to unlock", "NO_SESSION");
        var user = await users.FindByUuidAsync(current.UserUuid, ct);
        if (user is null) return Result<UserSession>.Fail(Generic, "INVALID_CREDENTIALS");
        var ok = hasher.Verify(secret, user.PasswordHash) || (user.PinHash is not null && hasher.Verify(secret, user.PinHash));
        return await CompleteAsync(user, ok, "unlock", ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (session.Current is { } s) await WriteAuditAsync(s.UserUuid, "auth.logout", ct);
        session.SignOut();
    }

    private async Task<Result<UserSession>> CompleteAsync(User user, bool secretOk, string method, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (user.LockedUntil is { } until && until > now)
            return Result<UserSession>.Fail($"Account locked. Try again after {until.ToLocalTime():t}.", "LOCKED");
        if (!user.IsActive || user.IsDeleted)
            return Result<UserSession>.Fail("This account is disabled", "DISABLED");

        if (!secretOk)
        {
            user.FailedLoginCount++;
            var locked = user.FailedLoginCount >= options.MaxFailedAttempts;
            if (locked) { user.LockedUntil = now.AddMinutes(options.LockoutMinutes); user.FailedLoginCount = 0; }
            await users.SaveLoginStateAsync(user, ct);
            await WriteAuditAsync(user.Uuid, locked ? "auth.account_locked" : $"auth.{method}_failed", ct);
            log.LogWarning("Failed {Method} login for {User}", method, user.Username);
            return Result<UserSession>.Fail(method == "password" ? Generic : "Incorrect PIN", "INVALID_CREDENTIALS");
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        await users.SaveLoginStateAsync(user, ct);

        var (roles, perms) = await users.GetAccessAsync(user.Uuid, ct);
        if (roles.Count == 0) return Result<UserSession>.Fail("This account has no role assigned. Ask a manager.", "NO_ROLE");

        var s = new UserSession(user.Uuid, user.Username, user.FullName, user.MustChangePassword, roles, perms.ToHashSet(), now);
        session.SignIn(s);
        await WriteAuditAsync(user.Uuid, $"auth.{method}_login", ct);
        log.LogInformation("{User} signed in via {Method}", user.Username, method);
        return Result<UserSession>.Ok(s);
    }

    private Task WriteAuditAsync(string userUuid, string action, CancellationToken ct) =>
        audit.AddAsync(new AuditLog
        {
            UserUuid = userUuid, Action = action, EntityType = "users", EntityUuid = userUuid,
            OccurredAt = clock.UtcNow, BranchUuid = device.BranchUuid, UserAgent = $"desktop:{device.DeviceId}",
        }, ct);
}
