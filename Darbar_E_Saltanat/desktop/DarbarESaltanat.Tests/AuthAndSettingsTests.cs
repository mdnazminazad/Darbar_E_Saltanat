using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Domain.Enums;
using DarbarESaltanat.Infrastructure.Persistence;
using DarbarESaltanat.Infrastructure.Repositories;
using DarbarESaltanat.Infrastructure.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarbarESaltanat.Tests;

public class AuthAndSettingsTests
{
    private const string AdminPassword = "ChangeMe@123"; // seeded default (must be changed at first login)

    private static (AuthService Auth, CurrentSession Session, SettingsService Settings) Build(TestDb t)
    {
        var session = new CurrentSession();
        var auth = new AuthService(new UserRepository(t.Factory), new BcryptPasswordHasher(4), session, new AuditRepository(t.Factory),
            t.Clock, t.Device, new AuthOptions { MaxFailedAttempts = 3, LockoutMinutes = 15 }, NullLogger<AuthService>.Instance);
        var settings = new SettingsService(new SettingsRepository(t.Factory), session, t.Device, t.Clock);
        return (auth, session, settings);
    }

    [Fact]
    public async Task Seeded_admin_can_log_in_offline_and_gets_every_permission()
    {
        await using var t = await TestDb.CreateAsync();
        var (auth, session, _) = Build(t);
        var r = await auth.LoginAsync(new LoginRequest("admin", AdminPassword));
        r.Succeeded.Should().BeTrue(r.Error);
        r.Value!.IsSuperAdmin.Should().BeTrue();
        r.Value.MustChangePassword.Should().BeTrue();
        session.Has("anything.at.all").Should().BeTrue();
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_generic_error()
    {
        await using var t = await TestDb.CreateAsync();
        var (auth, _, _) = Build(t);
        var wrong = await auth.LoginAsync(new LoginRequest("admin", "nope"));
        var unknown = await auth.LoginAsync(new LoginRequest("ghost", "nope"));
        wrong.Succeeded.Should().BeFalse();
        wrong.Error.Should().Be(unknown.Error);
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_and_login_bookkeeping_is_not_synced()
    {
        await using var t = await TestDb.CreateAsync();
        var (auth, _, _) = Build(t);
        for (var i = 0; i < 3; i++) await auth.LoginAsync(new LoginRequest("admin", "bad"));
        var r = await auth.LoginAsync(new LoginRequest("admin", AdminPassword));
        r.ErrorCode.Should().Be("LOCKED");

        t.Clock.UtcNow = t.Clock.UtcNow.AddMinutes(16);
        (await auth.LoginAsync(new LoginRequest("admin", AdminPassword))).Succeeded.Should().BeTrue();

        await using var db = t.Db();
        (await db.SyncOutbox.CountAsync(o => o.EntityType == "users")).Should().Be(0, "failed-login counters are local-only");
        (await db.SyncOutbox.CountAsync(o => o.EntityType == "audit_logs")).Should().BeGreaterThan(0, "login attempts are audited and synced");
    }

    [Fact]
    public async Task Validation_errors_are_returned_without_touching_the_database()
    {
        await using var t = await TestDb.CreateAsync();
        var (auth, _, _) = Build(t);
        var r = await auth.LoginAsync(new LoginRequest("", ""));
        r.ErrorCode.Should().Be("VALIDATION");
        var pin = await auth.PinLoginAsync(new PinLoginRequest("x", "12"));
        pin.ErrorCode.Should().Be("VALIDATION");
    }

    [Fact]
    public async Task Pin_login_works_for_users_with_a_pin()
    {
        await using var t = await TestDb.CreateAsync();
        var (auth, _, _) = Build(t);
        var hasher = new BcryptPasswordHasher(4);
        await using (var db = t.Db())
        {
            var admin = await db.Users.SingleAsync(u => u.Username == "admin");
            admin.PinHash = hasher.Hash("2468");
            await db.SaveChangesAsync();
        }
        var uuid = (await new UserRepository(t.Factory).FindByLoginAsync("admin"))!.Uuid;
        (await auth.PinLoginAsync(new PinLoginRequest(uuid, "1111"))).Succeeded.Should().BeFalse();
        (await auth.PinLoginAsync(new PinLoginRequest(uuid, "2468"))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Locked_session_requires_reauthentication_and_blocks_permissions()
    {
        await using var t = await TestDb.CreateAsync();
        var (auth, session, _) = Build(t);
        await auth.LoginAsync(new LoginRequest("admin", AdminPassword));
        session.Lock();
        session.Has("settings.manage").Should().BeFalse();
        (await auth.UnlockAsync("wrong")).Succeeded.Should().BeFalse();
        (await auth.UnlockAsync(AdminPassword)).Succeeded.Should().BeTrue();
        session.IsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_settings_requires_permission_validates_and_queues_changes()
    {
        await using var t = await TestDb.CreateAsync();
        var (auth, _, settings) = Build(t);

        var noSession = () => settings.SaveAsync(new RestaurantSettings());
        await noSession.Should().ThrowAsync<PermissionDeniedException>();

        await auth.LoginAsync(new LoginRequest("admin", AdminPassword));
        var current = await settings.GetAsync();
        current.VatRate.Should().Be(5m);

        (await settings.SaveAsync(current with { VatRate = 150 })).ErrorCode.Should().Be("VALIDATION");

        (await settings.SaveAsync(current with { VatRate = 7.5m, ReceiptFooter = "Dhonnobad" })).Succeeded.Should().BeTrue();
        (await settings.GetAsync(refresh: true)).VatRate.Should().Be(7.5m);

        await using var db = t.Db();
        var queued = await db.SyncOutbox.Where(o => o.EntityType == "settings").ToListAsync();
        queued.Should().HaveCount(2, "only changed keys are queued");
        queued.Should().OnlyContain(o => o.Operation == SyncOperation.Update && o.BaseVersion == 1);
        (await db.SyncOutbox.CountAsync(o => o.EntityType == "audit_logs" && o.PayloadJson.Contains("settings.update"))).Should().Be(1);
    }

    [Theory]
    [InlineData("BranchUuid", "branch_uuid")]
    [InlineData("SettingKey", "setting_key")]
    [InlineData("Uuid", "uuid")]
    public void Column_names_are_snake_case(string prop, string column) => Naming.ToSnake(prop).Should().Be(column);
}
