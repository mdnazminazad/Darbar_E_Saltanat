namespace DarbarESaltanat.Desktop.Services;

public static class PageKeys
{
    public const string Dashboard = "dashboard";
    public const string Pos = "pos";
    public const string Tables = "tables";
    public const string Orders = "orders";
    public const string Menu = "menu";
    public const string Settings = "settings";
    public const string Sync = "sync";
    public const string Users = "users";
    public const string Roles = "roles";
}

/// <summary>Lets one page open another (e.g. Tables → POS with an order) without knowing the shell.</summary>
public sealed class Navigator
{
    public event Action<string, object?>? Requested;
    public void Go(string pageKey, object? argument = null) => Requested?.Invoke(pageKey, argument);
}

/// <summary>Pages that need to load data (optionally with an argument) after navigation.</summary>
public interface IPage
{
    Task LoadAsync(object? argument);
}
