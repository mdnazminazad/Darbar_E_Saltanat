using DarbarESaltanat.Domain.Entities;

namespace DarbarESaltanat.Application.Users;

public sealed record UserListItem(
    string Uuid,
    string Username,
    string? Email,
    string FullName,
    string? Phone,
    bool IsActive,
    bool MustChangePassword,
    string RoleUuid,
    string RoleCode,
    string RoleName,
    string? BranchUuid
);

public sealed record RoleItem(
    string Uuid,
    string Code,
    string Name,
    string? Description,
    bool IsSystem,
    int PermissionCount
);

public sealed record CreateUserDto(
    string Username,
    string? Email,
    string FullName,
    string? Phone,
    string Password,
    string RoleUuid,
    string? BranchUuid = null,
    bool IsActive = true,
    bool MustChangePassword = false
);

public sealed record UpdateUserDto(
    string Uuid,
    string FullName,
    string? Email,
    string? Phone,
    string? RoleUuid,
    string? BranchUuid,
    bool IsActive,
    string? Username = null,
    string? NewPassword = null,
    bool? MustChangePassword = null
);

public interface IUserService
{
    Task<IReadOnlyList<UserListItem>> ListUsersAsync(string? search = null, string? roleUuid = null, bool? activeOnly = null, CancellationToken ct = default);
    Task<IReadOnlyList<RoleItem>> ListRolesAsync(CancellationToken ct = default);
    Task<UserListItem> CreateUserAsync(CreateUserDto dto, CancellationToken ct = default);
    Task UpdateUserAsync(UpdateUserDto dto, CancellationToken ct = default);
    Task ToggleStatusAsync(string userUuid, bool isActive, CancellationToken ct = default);
    Task ResetPasswordAsync(string userUuid, string newPassword, bool mustChangePassword = true, CancellationToken ct = default);
    Task DeleteUserAsync(string userUuid, CancellationToken ct = default);
}
