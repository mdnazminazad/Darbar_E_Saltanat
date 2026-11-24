using System.Text.Json;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Security;

namespace DarbarESaltanat.Application.Users;

public sealed class UserService(
    IUnitOfWorkFactory uowFactory,
    IPasswordHasher hasher,
    ICurrentSession session,
    IDeviceContext device,
    IClock clock) : IUserService
{
    public static void ValidatePasswordComplexity(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new InvalidOperationException("Password must be at least 8 characters long.");
        if (!password.Any(char.IsUpper))
            throw new InvalidOperationException("Password must contain at least one uppercase letter.");
        if (!password.Any(char.IsLower))
            throw new InvalidOperationException("Password must contain at least one lowercase letter.");
        if (!password.Any(char.IsDigit))
            throw new InvalidOperationException("Password must contain at least one number.");
        if (!password.Any(ch => !char.IsLetterOrDigit(ch)))
            throw new InvalidOperationException("Password must contain at least one special character.");
    }

    private void EnsurePermission()
    {
        if (!session.Has(Permissions.UsersManage) && !session.Has("users.create") && !session.Has("users.update"))
            throw new PermissionDeniedException("You do not have permission to manage users.");
    }

    public async Task<IReadOnlyList<UserListItem>> ListUsersAsync(string? search = null, string? roleUuid = null, bool? activeOnly = null, CancellationToken ct = default)
    {
        await using var uow = uowFactory.Begin();
        var allUsers = await uow.ListAsync<User>(u => u.DeletedAt == null, ct);
        var allRoles = await uow.ListAsync<Role>(r => r.DeletedAt == null, ct);
        var allUserRoles = await uow.ListAsync<UserRole>(ur => ur.DeletedAt == null, ct);

        var roleMap = allRoles.ToDictionary(r => r.Uuid);
        var userRoleMap = allUserRoles
            .GroupBy(ur => ur.UserUuid)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault()?.RoleUuid);

        var list = new List<UserListItem>();
        var s = search?.Trim().ToLower();

        foreach (var u in allUsers)
        {
            if (activeOnly == true && !u.IsActive) continue;
            if (!string.IsNullOrEmpty(s))
            {
                var match = u.Username.ToLower().Contains(s)
                            || u.FullName.ToLower().Contains(s)
                            || (u.Email != null && u.Email.ToLower().Contains(s))
                            || (u.Phone != null && u.Phone.Contains(s));
                if (!match) continue;
            }

            var rUuid = userRoleMap.TryGetValue(u.Uuid, out var rId) ? rId : null;
            if (roleUuid != null && rUuid != roleUuid) continue;

            var role = rUuid != null && roleMap.TryGetValue(rUuid, out var foundRole) ? foundRole : null;

            list.Add(new UserListItem(
                u.Uuid,
                u.Username,
                u.Email,
                u.FullName,
                u.Phone,
                u.IsActive,
                u.MustChangePassword,
                rUuid ?? "",
                role?.Code ?? "STAFF",
                role?.Name ?? "Staff",
                u.BranchUuid
            ));
        }

        return list.OrderBy(u => u.FullName).ToList();
    }

    public async Task<IReadOnlyList<RoleItem>> ListRolesAsync(CancellationToken ct = default)
    {
        await using var uow = uowFactory.Begin();
        var roles = await uow.ListAsync<Role>(r => r.DeletedAt == null, ct);
        var rolePerms = await uow.ListAsync<RolePermission>(rp => rp.DeletedAt == null, ct);

        var counts = rolePerms.GroupBy(rp => rp.RoleUuid).ToDictionary(g => g.Key, g => g.Count());

        return roles.Select(r => new RoleItem(
            r.Uuid,
            r.Code,
            r.Name,
            r.Description,
            r.IsSystem,
            counts.TryGetValue(r.Uuid, out var c) ? c : 0
        )).OrderBy(r => r.Name).ToList();
    }

    public async Task<UserListItem> CreateUserAsync(CreateUserDto dto, CancellationToken ct = default)
    {
        EnsurePermission();
        if (string.IsNullOrWhiteSpace(dto.Username)) throw new InvalidOperationException("Username is required.");
        if (string.IsNullOrWhiteSpace(dto.FullName)) throw new InvalidOperationException("Full name is required.");
        ValidatePasswordComplexity(dto.Password);

        var username = dto.Username.Trim();
        var email = dto.Email?.Trim();

        await using var uow = uowFactory.Begin();
        var existing = await uow.ListAsync<User>(u => u.DeletedAt == null, ct);
        if (existing.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Username '{username}' is already in use.");
        if (!string.IsNullOrEmpty(email) && existing.Any(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Email '{email}' is already in use.");

        var branchUuid = dto.BranchUuid ?? device.BranchUuid;
        var now = clock.UtcNow;

        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Username = username,
            Email = email,
            FullName = dto.FullName.Trim(),
            Phone = dto.Phone?.Trim(),
            PasswordHash = hasher.Hash(dto.Password),
            IsActive = dto.IsActive,
            MustChangePassword = dto.MustChangePassword,
            BranchUuid = branchUuid,
            CreatedAt = now,
            UpdatedAt = now,
            SyncVersion = 0
        };

        uow.Add(user);

        Role? assignedRole = null;
        if (!string.IsNullOrEmpty(dto.RoleUuid))
        {
            assignedRole = await uow.FindAsync<Role>(dto.RoleUuid, ct);
            uow.Add(new UserRole
            {
                Uuid = Guid.NewGuid().ToString(),
                UserUuid = user.Uuid,
                RoleUuid = dto.RoleUuid,
                BranchUuid = branchUuid,
                CreatedAt = now,
                UpdatedAt = now,
                SyncVersion = 0
            });
        }

        uow.Add(new AuditLog
        {
            Uuid = Guid.NewGuid().ToString(),
            UserUuid = session.Current?.UserUuid,
            Action = "user.create",
            EntityType = "user",
            EntityUuid = user.Uuid,
            NewValues = JsonSerializer.Serialize(new { username = user.Username, fullName = user.FullName, role = assignedRole?.Code, branch = branchUuid }),
            OccurredAt = now,
            BranchUuid = branchUuid,
            CreatedAt = now,
            UpdatedAt = now
        });

        await uow.CommitAsync(ct);

        return new UserListItem(
            user.Uuid,
            user.Username,
            user.Email,
            user.FullName,
            user.Phone,
            user.IsActive,
            user.MustChangePassword,
            dto.RoleUuid,
            assignedRole?.Code ?? "STAFF",
            assignedRole?.Name ?? "Staff",
            user.BranchUuid
        );
    }

    public async Task UpdateUserAsync(UpdateUserDto dto, CancellationToken ct = default)
    {
        EnsurePermission();
        if (string.IsNullOrWhiteSpace(dto.FullName)) throw new InvalidOperationException("Full name is required.");

        await using var uow = uowFactory.Begin();
        var user = await uow.FindAsync<User>(dto.Uuid, ct) ?? throw new KeyNotFoundException("User not found.");

        if (session.Current?.IsSuperAdmin != true && dto.Uuid == session.Current?.UserUuid && dto.RoleUuid != null)
        {
            var currentUr = (await uow.ListAsync<UserRole>(ur => ur.UserUuid == dto.Uuid && ur.DeletedAt == null, ct)).FirstOrDefault();
            if (currentUr != null && currentUr.RoleUuid != dto.RoleUuid)
                throw new PermissionDeniedException("You cannot edit your own role.");
        }

        var now = clock.UtcNow;
        var oldValues = JsonSerializer.Serialize(new { username = user.Username, fullName = user.FullName, email = user.Email, phone = user.Phone, isActive = user.IsActive });

        // Update username if provided and changed
        if (!string.IsNullOrWhiteSpace(dto.Username) && !string.Equals(dto.Username.Trim(), user.Username, StringComparison.OrdinalIgnoreCase))
        {
            var newUsername = dto.Username.Trim();
            if (newUsername.Length < 3 || newUsername.Length > 60)
                throw new InvalidOperationException("Username must be between 3 and 60 characters.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(newUsername, "^[a-zA-Z0-9._-]+$"))
                throw new InvalidOperationException("Username may only contain letters, digits, dots, dashes, and underscores.");

            var allUsers = await uow.ListAsync<User>(u => u.DeletedAt == null && u.Uuid != user.Uuid, ct);
            if (allUsers.Any(u => string.Equals(u.Username, newUsername, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Username '{newUsername}' is already taken.");

            var oldUsername = user.Username;
            user.Username = newUsername;

            uow.Add(new AuditLog
            {
                Uuid = Guid.NewGuid().ToString(),
                UserUuid = session.Current?.UserUuid,
                Action = "users.username_change",
                EntityType = "users",
                EntityUuid = user.Uuid,
                OldValues = JsonSerializer.Serialize(new { username = oldUsername }),
                NewValues = JsonSerializer.Serialize(new { username = newUsername }),
                OccurredAt = now,
                BranchUuid = user.BranchUuid,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        // Update password if specified
        if (!string.IsNullOrWhiteSpace(dto.NewPassword))
        {
            ValidatePasswordComplexity(dto.NewPassword);
            if (hasher.Verify(dto.NewPassword, user.PasswordHash))
                throw new InvalidOperationException("New password cannot be the same as the current password.");

            user.PasswordHash = hasher.Hash(dto.NewPassword);
            user.MustChangePassword = dto.MustChangePassword ?? true;
            user.FailedLoginCount = 0;
            user.LockedUntil = null;

            uow.Add(new AuditLog
            {
                Uuid = Guid.NewGuid().ToString(),
                UserUuid = session.Current?.UserUuid,
                Action = "user.password_change",
                EntityType = "user",
                EntityUuid = user.Uuid,
                NewValues = JsonSerializer.Serialize(new { mustChangePassword = user.MustChangePassword }),
                OccurredAt = now,
                BranchUuid = user.BranchUuid,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else if (dto.MustChangePassword.HasValue)
        {
            user.MustChangePassword = dto.MustChangePassword.Value;
        }

        user.FullName = dto.FullName.Trim();
        user.Email = dto.Email?.Trim();
        user.Phone = dto.Phone?.Trim();
        user.IsActive = dto.IsActive;
        user.UpdatedAt = now;

        if (!string.IsNullOrEmpty(dto.RoleUuid))
        {
            var userRoles = await uow.ListAsync<UserRole>(ur => ur.UserUuid == dto.Uuid && ur.DeletedAt == null, ct);
            var activeRole = userRoles.FirstOrDefault();
            if (activeRole == null)
            {
                uow.Add(new UserRole
                {
                    Uuid = Guid.NewGuid().ToString(),
                    UserUuid = user.Uuid,
                    RoleUuid = dto.RoleUuid,
                    BranchUuid = user.BranchUuid,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else if (activeRole.RoleUuid != dto.RoleUuid)
            {
                activeRole.RoleUuid = dto.RoleUuid;
                activeRole.UpdatedAt = now;
            }
        }

        uow.Add(new AuditLog
        {
            Uuid = Guid.NewGuid().ToString(),
            UserUuid = session.Current?.UserUuid,
            Action = "user.update",
            EntityType = "user",
            EntityUuid = user.Uuid,
            OldValues = oldValues,
            NewValues = JsonSerializer.Serialize(new { username = user.Username, fullName = user.FullName, email = user.Email, phone = user.Phone, isActive = user.IsActive, mustChangePassword = user.MustChangePassword }),
            OccurredAt = now,
            BranchUuid = user.BranchUuid,
            CreatedAt = now,
            UpdatedAt = now
        });

        await uow.CommitAsync(ct);
    }

    public async Task ToggleStatusAsync(string userUuid, bool isActive, CancellationToken ct = default)
    {
        EnsurePermission();
        await using var uow = uowFactory.Begin();
        var user = await uow.FindAsync<User>(userUuid, ct) ?? throw new KeyNotFoundException("User not found.");

        if (!isActive)
        {
            if (userUuid == session.Current?.UserUuid && session.Current?.IsSuperAdmin != true)
                throw new PermissionDeniedException("You cannot deactivate your own account.");

            var userRole = (await uow.ListAsync<UserRole>(ur => ur.UserUuid == userUuid && ur.DeletedAt == null, ct)).FirstOrDefault();
            if (userRole != null)
            {
                var role = await uow.FindAsync<Role>(userRole.RoleUuid, ct);
                if (role != null && (role.Code == "SUPER_ADMIN" || role.Code == "super_admin"))
                {
                    var allRoles = await uow.ListAsync<UserRole>(ur => ur.RoleUuid == role.Uuid && ur.DeletedAt == null, ct);
                    var activeSuperAdmins = 0;
                    foreach (var ur in allRoles)
                    {
                        var u = await uow.FindAsync<User>(ur.UserUuid, ct);
                        if (u != null && u.IsActive && u.DeletedAt == null) activeSuperAdmins++;
                    }
                    if (activeSuperAdmins <= 1)
                        throw new InvalidOperationException("Cannot deactivate the final active Super Admin account.");
                }
            }
        }

        var now = clock.UtcNow;
        user.IsActive = isActive;
        user.UpdatedAt = now;

        uow.Add(new AuditLog
        {
            Uuid = Guid.NewGuid().ToString(),
            UserUuid = session.Current?.UserUuid,
            Action = isActive ? "user.activate" : "user.deactivate",
            EntityType = "user",
            EntityUuid = user.Uuid,
            NewValues = JsonSerializer.Serialize(new { isActive = user.IsActive }),
            OccurredAt = now,
            BranchUuid = user.BranchUuid,
            CreatedAt = now,
            UpdatedAt = now
        });

        await uow.CommitAsync(ct);
    }

    public async Task ResetPasswordAsync(string userUuid, string newPassword, bool mustChangePassword = true, CancellationToken ct = default)
    {
        EnsurePermission();
        ValidatePasswordComplexity(newPassword);

        await using var uow = uowFactory.Begin();
        var user = await uow.FindAsync<User>(userUuid, ct) ?? throw new KeyNotFoundException("User not found.");

        if (hasher.Verify(newPassword, user.PasswordHash))
            throw new InvalidOperationException("New password cannot be the same as the current password.");

        var now = clock.UtcNow;
        user.PasswordHash = hasher.Hash(newPassword);
        user.MustChangePassword = mustChangePassword;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.UpdatedAt = now;

        uow.Add(new AuditLog
        {
            Uuid = Guid.NewGuid().ToString(),
            UserUuid = session.Current?.UserUuid,
            Action = "user.reset_password",
            EntityType = "user",
            EntityUuid = user.Uuid,
            NewValues = JsonSerializer.Serialize(new { mustChangePassword = mustChangePassword }),
            OccurredAt = now,
            BranchUuid = user.BranchUuid,
            CreatedAt = now,
            UpdatedAt = now
        });

        await uow.CommitAsync(ct);
    }

    public async Task DeleteUserAsync(string userUuid, CancellationToken ct = default)
    {
        EnsurePermission();
        await using var uow = uowFactory.Begin();
        var user = await uow.FindAsync<User>(userUuid, ct) ?? throw new KeyNotFoundException("User not found.");

        var userRole = (await uow.ListAsync<UserRole>(ur => ur.UserUuid == userUuid && ur.DeletedAt == null, ct)).FirstOrDefault();
        if (userRole != null)
        {
            var role = await uow.FindAsync<Role>(userRole.RoleUuid, ct);
            if (role != null && (role.Code == "SUPER_ADMIN" || role.Code == "super_admin"))
            {
                var allRoles = await uow.ListAsync<UserRole>(ur => ur.RoleUuid == role.Uuid && ur.DeletedAt == null, ct);
                var activeSuperAdmins = 0;
                foreach (var ur in allRoles)
                {
                    var u = await uow.FindAsync<User>(ur.UserUuid, ct);
                    if (u != null && u.IsActive && u.DeletedAt == null) activeSuperAdmins++;
                }
                if (activeSuperAdmins <= 1)
                    throw new InvalidOperationException("Cannot delete the final active Super Admin account.");
            }
        }

        var now = clock.UtcNow;
        uow.Delete(user);

        var assignedRoles = await uow.ListAsync<UserRole>(ur => ur.UserUuid == userUuid && ur.DeletedAt == null, ct);
        foreach (var ar in assignedRoles) uow.Delete(ar);

        uow.Add(new AuditLog
        {
            Uuid = Guid.NewGuid().ToString(),
            UserUuid = session.Current?.UserUuid,
            Action = "user.delete",
            EntityType = "user",
            EntityUuid = user.Uuid,
            OccurredAt = now,
            BranchUuid = user.BranchUuid,
            CreatedAt = now,
            UpdatedAt = now
        });

        await uow.CommitAsync(ct);
    }
}
