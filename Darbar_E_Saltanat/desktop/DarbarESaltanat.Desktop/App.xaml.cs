using System.IO;
using System.Windows;
using System.Windows.Threading;
using DarbarESaltanat.Application;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Desktop.Services;
using DarbarESaltanat.Desktop.ViewModels;
using DarbarESaltanat.Infrastructure;
using DarbarESaltanat.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DarbarESaltanat.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    public static IServiceProvider Services { get; private set; } = default!;

    /// <summary>%LOCALAPPDATA%\DarbarESaltanat — database, logs and backups live here.</summary>
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DarbarESaltanat");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppTheme.Initialize();   // light/dark choice of this computer, before the first window
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
        var dbPath = Path.Combine(DataDir, "darbar.db");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(DataDir, "logs", "desktop-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Fatal(a.ExceptionObject as Exception, "Unhandled domain exception");
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error(a.Exception, "Unobserved task exception"); a.SetObserved(); };

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => { l.ClearProviders(); l.AddSerilog(Log.Logger, dispose: false); })
                .ConfigureServices(s =>
                {
                    s.AddApplication();
                    s.AddInfrastructure(dbPath);
                    s.AddSingleton<WindowCoordinator>();
                    s.AddSingleton<InactivityMonitor>();
                    s.AddSingleton<SyncStatusMonitor>();
                    s.AddTransient<LoginViewModel>();
                    s.AddTransient<MainViewModel>();
                    s.AddTransient<DashboardViewModel>();
                    s.AddTransient<SettingsViewModel>();
                    s.AddTransient<PosViewModel>();
                    s.AddTransient<TablesViewModel>();
                    s.AddTransient<OrdersViewModel>();
                    s.AddTransient<MenuManagementViewModel>();
                    s.AddTransient<SyncViewModel>();
                    s.AddTransient<UsersViewModel>();
                    s.AddTransient<RolesViewModel>();
                    s.AddSingleton<Navigator>();
                    s.AddSingleton<DialogService>();
                    s.AddSingleton<PrintService>();
                    s.AddSingleton<DarbarESaltanat.Application.Printing.IPrintService>(sp => sp.GetRequiredService<PrintService>());
                })
                .Build();
            Services = _host.Services;

            // Create/upgrade the local DB BEFORE anything reads it. Works with no internet.
            await using (var conn = new SqliteConnection(Services.GetRequiredService<SqliteConnectionString>().Value))
            {
                await Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(conn);
                Services.GetRequiredService<DeviceContext>().Load(conn);
            }
            // Settings arriving from the server (pull) refresh the open screens.
            Services.GetRequiredService<DarbarESaltanat.Application.Sync.SyncEngine>().RemoteChangesApplied += (_, tables) =>
            {
                if (tables.Contains("settings"))
                    Current.Dispatcher.InvokeAsync(async () => await Services.GetRequiredService<DarbarESaltanat.Application.Settings.SettingsService>().ReloadAfterSyncAsync());
                if (tables.Contains("users"))
                {
                    Current.Dispatcher.InvokeAsync(async () =>
                    {
                        var session = Services.GetRequiredService<DarbarESaltanat.Application.Auth.ICurrentSession>();
                        if (session.Current is { } curr)
                        {
                            var userRepo = Services.GetRequiredService<DarbarESaltanat.Application.Abstractions.IUserRepository>();
                            var user = await userRepo.FindByUuidAsync(curr.UserUuid);
                            if (user == null || !user.IsActive || user.IsDeleted)
                            {
                                Log.Warning("Current user {Uuid} was deactivated or deleted on server. Signing out terminal.", curr.UserUuid);
                                session.SignOut();
                                Services.GetRequiredService<WindowCoordinator>().ShowLogin();
                                MessageBox.Show("Your account has been deactivated by an administrator. This terminal session has been closed.",
                                    "Session Terminated", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                    });
                }
            };
            DarbarESaltanat.Application.Sync.SyncEngine.AppVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            await _host.StartAsync();
            Log.Information("Darbar_E_Saltanat desktop started. Data dir {Dir}", DataDir);
            Services.GetRequiredService<WindowCoordinator>().ShowLogin();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show($"The application could not start:\n\n{ex.Message}\n\nDetails were written to the log in {DataDir}\\logs.",
                "Darbar_E_Saltanat", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool _errorBoxOpen;

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (e.Exception is PermissionDeniedException pd)
        {
            // A locked terminal refuses everything by design — the lock screen already explains it, so no dialog.
            if (Services?.GetService<DarbarESaltanat.Application.Auth.ICurrentSession>()?.IsLocked == true) return;
            ShowOneErrorBox(() => MessageBox.Show(pd.Message, "Permission required", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        else
        {
            Log.Error(e.Exception, "Unhandled UI exception");
            ShowOneErrorBox(() => MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nYour saved data is safe. The error was logged.",
                "Darbar_E_Saltanat", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    /// <summary>At most one error box at a time: a failure that repeats in the background must not stack dozens of dialogs.</summary>
    private static void ShowOneErrorBox(Action show)
    {
        if (_errorBoxOpen) return;
        _errorBoxOpen = true;
        try { show(); } finally { _errorBoxOpen = false; }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null) { await _host.StopAsync(TimeSpan.FromSeconds(5)); _host.Dispose(); }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
