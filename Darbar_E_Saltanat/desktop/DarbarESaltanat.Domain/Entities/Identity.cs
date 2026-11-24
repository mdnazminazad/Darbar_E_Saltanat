using DarbarESaltanat.Domain.Common;

namespace DarbarESaltanat.Domain.Entities;

[SyncTable("branches")]
public class Branch : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string Timezone { get; set; } = "Asia/Dhaka";
    public string Currency { get; set; } = "BDT";
    public bool IsActive { get; set; } = true;
}

[SyncTable("roles")]
public class Role : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
}

[SyncTable("permissions")]
public class Permission : SyncEntity
{
    public string Code { get; set; } = "";
    public string Module { get; set; } = "";
    public string? Description { get; set; }
}

[SyncTable("role_permissions")]
public class RolePermission : SyncEntity
{
    public string RoleUuid { get; set; } = "";
    public string PermissionUuid { get; set; } = "";
}

[SyncTable("users")]
public class User : SyncEntity
{
    public string Username { get; set; } = "";
    public string? Email { get; set; }
    /// <summary>bcrypt hash (same algorithm as the server, so synced users can log in offline).</summary>
    public string PasswordHash { get; set; } = "";
    public string? PinHash { get; set; }
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

[SyncTable("user_roles")]
public class UserRole : SyncEntity
{
    public string UserUuid { get; set; } = "";
    public string RoleUuid { get; set; } = "";
}

[SyncTable("settings")]
public class Setting : SyncEntity
{
    public string SettingKey { get; set; } = "";
    public string? SettingValue { get; set; }
    public string ValueType { get; set; } = "STRING";
    public string GroupName { get; set; } = "general";
}

[SyncTable("audit_logs")]
[AppendOnly]
public class AuditLog : SyncEntity
{
    public string? UserUuid { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityUuid { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredAt { get; set; }
}
