using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Users;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Domain.Entities;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed record PermissionGroup(string Module, List<string> Permissions);

public sealed partial class RolesViewModel(IUserService userService, IUnitOfWorkFactory uowFactory) : ObservableObject, IPage
{
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private RoleItem? _selectedRole;

    public ObservableCollection<RoleItem> Roles { get; } = [];
    public ObservableCollection<PermissionGroup> PermissionGroups { get; } = [];

    public async Task LoadAsync(object? argument) => await RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            var list = await userService.ListRolesAsync();
            Roles.Clear();
            foreach (var r in list) Roles.Add(r);
            SelectedRole = Roles.FirstOrDefault();
        }
        finally
        {
            Busy = false;
        }
    }

    partial void OnSelectedRoleChanged(RoleItem? value)
    {
        if (value is not null) _ = LoadPermissionsForRoleAsync(value.Uuid);
        else PermissionGroups.Clear();
    }

    private async Task LoadPermissionsForRoleAsync(string roleUuid)
    {
        await using var uow = uowFactory.Begin();
        var allPerms = await uow.ListAsync<Permission>(p => p.DeletedAt == null);
        var rolePerms = await uow.ListAsync<RolePermission>(rp => rp.RoleUuid == roleUuid && rp.DeletedAt == null);
        var permUuids = rolePerms.Select(rp => rp.PermissionUuid).ToHashSet();

        var granted = allPerms.Where(p => permUuids.Contains(p.Uuid)).ToList();

        PermissionGroups.Clear();
        var grouped = granted.GroupBy(p => p.Module).OrderBy(g => g.Key);
        foreach (var g in grouped)
        {
            PermissionGroups.Add(new PermissionGroup(g.Key.ToUpper(), g.Select(p => $"{p.Code}: {p.Description}").ToList()));
        }
    }
}
