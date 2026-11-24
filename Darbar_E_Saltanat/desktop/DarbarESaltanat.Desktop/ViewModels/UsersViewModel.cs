using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Users;
using DarbarESaltanat.Desktop.Services;

namespace DarbarESaltanat.Desktop.ViewModels;

public sealed partial class UsersViewModel(IUserService userService, ICurrentSession session) : ObservableObject, IPage
{
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _successMessage;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showDialog;
    [ObservableProperty] private string _dialogTitle = "Add New User";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditUsername))]
    [NotifyPropertyChangedFor(nameof(PasswordFieldLabel))]
    private bool _isEditing;

    public bool IsSuperAdmin => session.Current?.IsSuperAdmin == true;
    public bool CanEditUsername => !IsEditing || IsSuperAdmin;
    public string PasswordFieldLabel => IsEditing ? "Change Password (leave blank to keep current)" : "Initial Password *";

    // Form fields
    [ObservableProperty] private string _formUuid = "";
    [ObservableProperty] private string _formUsername = "";
    [ObservableProperty] private string _formFullName = "";
    [ObservableProperty] private string _formEmail = "";
    [ObservableProperty] private string _formPhone = "";
    [ObservableProperty] private string _formPassword = "";
    [ObservableProperty] private bool _formIsActive = true;
    [ObservableProperty] private bool _formMustChangePassword = false;
    [ObservableProperty] private RoleItem? _selectedRole;

    // Reset password dialog
    [ObservableProperty] private bool _showResetDialog;
    [ObservableProperty] private string _resetUserUuid = "";
    [ObservableProperty] private string _resetUsername = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private bool _resetMustChangePassword = true;

    public ObservableCollection<UserListItem> Users { get; } = [];
    public ObservableCollection<RoleItem> Roles { get; } = [];

    public async Task LoadAsync(object? argument) => await RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        Busy = true;
        ErrorMessage = null;
        try
        {
            var roles = await userService.ListRolesAsync();
            Roles.Clear();
            foreach (var r in roles) Roles.Add(r);

            var users = await userService.ListUsersAsync(SearchText);
            Users.Clear();
            foreach (var u in users) Users.Add(u);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();

    partial void OnSelectedRoleChanged(RoleItem? value)
    {
        if (!IsEditing && value != null && (string.IsNullOrWhiteSpace(FormUsername) || FormUsername.Contains('-') || FormUsername.Contains('_')))
        {
            SuggestUsernameForRole(value);
        }
    }

    private void SuggestUsernameForRole(RoleItem role)
    {
        var prefix = role.Code.ToUpperInvariant() switch
        {
            "SUPER_ADMIN" => "ADM",
            "MANAGER" => "MGR",
            "CASHIER" => "CSH",
            "WAITER" => "WTR",
            "KITCHEN" => "KIT",
            "ACCOUNTANT" => "ACC",
            "INVENTORY_MANAGER" => "INV",
            _ => role.Code.Length >= 3 ? role.Code[..3].ToUpperInvariant() : "USR"
        };
        var existingCount = Users.Count(u => u.RoleName.Equals(role.Name, StringComparison.OrdinalIgnoreCase) || u.Username.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        FormUsername = $"{prefix}-{existingCount + 1:D2}";
    }

    [RelayCommand]
    public void OpenNewUserDialog()
    {
        DialogTitle = "Add New User";
        IsEditing = false;
        FormUuid = "";
        FormUsername = "";
        FormFullName = "";
        FormEmail = "";
        FormPhone = "";
        FormPassword = "";
        FormIsActive = true;
        FormMustChangePassword = false;
        SelectedRole = Roles.FirstOrDefault();
        if (SelectedRole != null)
        {
            SuggestUsernameForRole(SelectedRole);
        }
        ErrorMessage = null;
        SuccessMessage = null;
        ShowDialog = true;
    }

    [RelayCommand]
    public void OpenEditUserDialog(UserListItem user)
    {
        DialogTitle = $"Edit User: {user.Username}";
        IsEditing = true;
        FormUuid = user.Uuid;
        FormUsername = user.Username;
        FormFullName = user.FullName;
        FormEmail = user.Email ?? "";
        FormPhone = user.Phone ?? "";
        FormPassword = "";
        FormIsActive = user.IsActive;
        FormMustChangePassword = user.MustChangePassword;
        SelectedRole = Roles.FirstOrDefault(r => r.Uuid == user.RoleUuid) ?? Roles.FirstOrDefault();
        ErrorMessage = null;
        SuccessMessage = null;
        ShowDialog = true;
    }

    [RelayCommand]
    public void CloseDialog()
    {
        ShowDialog = false;
        ShowResetDialog = false;
        ErrorMessage = null;
    }

    [RelayCommand]
    public async Task SaveUserAsync()
    {
        Busy = true;
        ErrorMessage = null;
        try
        {
            if (IsEditing)
            {
                await userService.UpdateUserAsync(new UpdateUserDto(
                    FormUuid,
                    FormFullName,
                    string.IsNullOrWhiteSpace(FormEmail) ? null : FormEmail,
                    string.IsNullOrWhiteSpace(FormPhone) ? null : FormPhone,
                    SelectedRole?.Uuid,
                    null,
                    FormIsActive,
                    FormUsername,
                    string.IsNullOrWhiteSpace(FormPassword) ? null : FormPassword,
                    FormMustChangePassword
                ));
                SuccessMessage = $"User '{FormUsername}' updated successfully.";
            }
            else
            {
                if (SelectedRole == null)
                {
                    ErrorMessage = "Please select a role.";
                    return;
                }

                await userService.CreateUserAsync(new CreateUserDto(
                    FormUsername,
                    string.IsNullOrWhiteSpace(FormEmail) ? null : FormEmail,
                    FormFullName,
                    string.IsNullOrWhiteSpace(FormPhone) ? null : FormPhone,
                    FormPassword,
                    SelectedRole.Uuid,
                    null,
                    FormIsActive,
                    FormMustChangePassword
                ));
                SuccessMessage = $"User '{FormUsername}' created successfully.";
            }

            ShowDialog = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    public async Task ToggleStatusAsync(UserListItem user)
    {
        Busy = true;
        ErrorMessage = null;
        try
        {
            await userService.ToggleStatusAsync(user.Uuid, !user.IsActive);
            SuccessMessage = $"User '{user.Username}' is now {(user.IsActive ? "inactive" : "active")}.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    public void OpenResetDialog(UserListItem user)
    {
        ResetUserUuid = user.Uuid;
        ResetUsername = user.Username;
        NewPassword = "";
        ResetMustChangePassword = true;
        ErrorMessage = null;
        SuccessMessage = null;
        ShowResetDialog = true;
    }

    [RelayCommand]
    public async Task ExecuteResetPasswordAsync()
    {
        Busy = true;
        ErrorMessage = null;
        try
        {
            await userService.ResetPasswordAsync(ResetUserUuid, NewPassword, ResetMustChangePassword);
            SuccessMessage = $"Password for '{ResetUsername}' was updated successfully." +
                             (ResetMustChangePassword ? " The user must change it at next login." : "");
            ShowResetDialog = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteUserAsync(UserListItem user)
    {
        if (System.Windows.MessageBox.Show($"Are you sure you want to delete user '{user.Username}'?",
                "Confirm Delete", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        Busy = true;
        ErrorMessage = null;
        try
        {
            await userService.DeleteUserAsync(user.Uuid);
            SuccessMessage = $"User '{user.Username}' deleted.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
