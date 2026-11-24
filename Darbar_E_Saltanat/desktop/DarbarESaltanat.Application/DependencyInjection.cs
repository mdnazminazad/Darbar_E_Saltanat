using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Application.Customers;
using DarbarESaltanat.Application.Dashboard;
using DarbarESaltanat.Application.Menu;
using DarbarESaltanat.Application.Pos;
using DarbarESaltanat.Application.Settings;
using DarbarESaltanat.Application.Sync;
using DarbarESaltanat.Application.Tables;
using DarbarESaltanat.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace DarbarESaltanat.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ICurrentSession, CurrentSession>();
        services.AddSingleton(new AuthOptions());
        services.AddSingleton<AuthService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AuditFactory>();
        services.AddSingleton<OrderService>();
        services.AddSingleton<MenuService>();
        services.AddSingleton<TableService>();
        services.AddSingleton<CustomerService>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<SyncStatus>();
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<IUserService, UserService>();
        return services;
    }
}
