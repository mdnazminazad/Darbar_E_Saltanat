using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Infrastructure.Persistence;
using DarbarESaltanat.Infrastructure.Repositories;
using DarbarESaltanat.Infrastructure.Security;
using DarbarESaltanat.Infrastructure.Sync;
using DarbarESaltanat.Application.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DarbarESaltanat.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers SQLite persistence. Call <see cref="DatabaseInitializer"/> + DeviceContext.Load before first use.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string databasePath)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = databasePath, Cache = SqliteCacheMode.Private, ForeignKeys = true, Pooling = true }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(cs).AddInterceptors(new SqlitePragmaInterceptor()).Options;

        services.AddSingleton(options);
        services.AddSingleton(new SqliteConnectionString(cs));
        services.AddSingleton<SqliteWriteGate>();
        services.AddSingleton<DeviceContext>();
        services.AddSingleton<IDeviceContext>(sp => sp.GetRequiredService<DeviceContext>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher>(new BcryptPasswordHasher());
        if (OperatingSystem.IsWindows()) services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IAppDbContextFactory, AppDbContextFactory>();
        services.AddSingleton<IUnitOfWorkFactory, EfUnitOfWorkFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IAuditRepository, AuditRepository>();
        services.AddSingleton<ISyncStateStore, SyncStateStore>();
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10) })
            { Timeout = TimeSpan.FromSeconds(60) });
        services.AddSingleton<ISyncApi, HttpSyncApi>();
        services.AddSingleton<ISyncStore>(sp => new SqliteSyncStore(sp.GetRequiredService<SqliteConnectionString>(), sp.GetRequiredService<SqliteWriteGate>(), sp.GetService<ISecretProtector>()));
        services.AddSingleton<SyncWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncWorker>());
        return services;
    }
}

public sealed record SqliteConnectionString(string Value);
