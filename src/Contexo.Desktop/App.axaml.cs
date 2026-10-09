using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Contexo.App.About;
using Contexo.App.Services;
using Contexo.App.Shell;
using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Desktop.Platform.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Contexo.Desktop;

public sealed partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _singleInstance;
    private ThemeManager? _themeManager;
    private ILogger<App>? _logger;
    private NativeMenuItem? _pauseItem;
    private bool _errorDialogOpen;

    /// <summary>The application's services. Null until the desktop lifetime has started (and always null under the headless test lifetime).</summary>
    internal IServiceProvider? Services => _host?.Services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window can hide to the tray, so closing it must not end the process; Exit() ends it explicitly.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _singleInstance = SingleInstance.Acquire();
            if (_singleInstance.IsPrimary)
            {
                _host = BuildHost();
                _logger = _host.Services.GetRequiredService<ILogger<App>>();
                RegisterExceptionHandlers();
                desktop.Exit += (_, _) => Shutdown();
                var minimized = (desktop.Args ?? []).Contains("--minimized", StringComparer.OrdinalIgnoreCase);
                Dispatcher.UIThread.Post(async () => await StartAsync(desktop, minimized));
            }
            else
            {
                // Ask the running instance to show its window, then leave.
                _singleInstance.NotifyPrimaryAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Dispatcher.UIThread.Post(() => desktop.Shutdown());
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        var paths = new AppPaths();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "contexo-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(logger, dispose: true);

        builder.Services.AddSingleton<IAppPaths>(paths);
        builder.Services.AddContexoDesktop(); // before AddContexoCore: the platform activity monitor must win
        builder.Services.AddContexoCore();

        return builder.Build();
    }

    /// <summary>Start-up order: window objects, host, database, indexing, then show the window (unless started with --minimized).</summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, bool minimized)
    {
        var services = _host!.Services;
        try
        {
            var settings = services.GetRequiredService<ISettingsStore>();
            _themeManager = new ThemeManager(this, settings);

            var shell = services.GetRequiredService<ShellViewModel>();
            var window = new MainWindow { DataContext = shell };
            services.GetRequiredService<MainWindowProvider>().Window = window;
            services.GetRequiredService<TrayCoordinator>().Attach(window);

            var lifetime = services.GetRequiredService<IAppLifetime>();
            _singleInstance!.ActivationRequested += (_, _) => lifetime.ShowMainWindow();
            _singleInstance.StartListening();

            await _host.StartAsync();
            _logger!.LogInformation("Contexo started");

            var databaseReady = await InitializeDatabaseAsync(services, shell);
            ConfigureTrayMenu(services.GetRequiredService<IIndexingService>(), lifetime);

            if (databaseReady)
            {
                shell.StatusBar.Start();
                await StartIndexingAsync(services.GetRequiredService<IIndexingService>());
            }

            if (!minimized)
            {
                desktop.MainWindow = window;
                window.Show();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Start-up failed");
            desktop.Shutdown(1);
        }
    }

    private async Task<bool> InitializeDatabaseAsync(IServiceProvider services, ShellViewModel shell)
    {
        try
        {
            await services.GetRequiredService<IKnowledgeStore>().InitializeAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger!.LogError(ex, "Database initialisation failed");
            shell.ShowStartupError(new StartupErrorViewModel());
            return false;
        }
    }

    private async Task StartIndexingAsync(IIndexingService indexing)
    {
        try
        {
            await indexing.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger!.LogError(ex, "Could not start indexing");
        }
    }

    private void Shutdown()
    {
        try
        {
            var indexing = _host?.Services.GetService<IIndexingService>();
            indexing?.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Indexing did not stop cleanly");
        }

        _themeManager?.Dispose();
        _singleInstance?.Dispose();
        _host?.Dispose();
    }

    // ---- Tray icon -------------------------------------------------------------------------------------------

    private void ConfigureTrayMenu(IIndexingService indexing, IAppLifetime lifetime)
    {
        var menu = TrayIcon.GetIcons(this)?.FirstOrDefault()?.Menu;
        _pauseItem = menu?.Items.OfType<NativeMenuItem>().ElementAtOrDefault(1);
        UpdatePauseItem(indexing.Current.State);
        indexing.SnapshotChanged += (_, snapshot) => Dispatcher.UIThread.Post(() => UpdatePauseItem(snapshot.State));
    }

    private void UpdatePauseItem(IndexingState state)
    {
        if (_pauseItem is not null)
        {
            _pauseItem.Header = state == IndexingState.Paused ? "繼續處理" : "暫停處理";
        }
    }

    private void OnTrayClicked(object? sender, EventArgs e)
    {
        // Windows: left click opens the window. macOS shows the menu instead (the platform default), so this never fires there.
        if (OperatingSystem.IsWindows())
        {
            _host?.Services.GetService<IAppLifetime>()?.ShowMainWindow();
        }
    }

    private void OnOpenMenuClick(object? sender, EventArgs e) =>
        _host?.Services.GetService<IAppLifetime>()?.ShowMainWindow();

    private void OnPauseMenuClick(object? sender, EventArgs e)
    {
        var indexing = _host?.Services.GetService<IIndexingService>();
        if (indexing is null)
        {
            return;
        }

        if (indexing.Current.State == IndexingState.Paused)
        {
            indexing.Resume();
        }
        else
        {
            indexing.Pause();
        }
    }

    private void OnExitMenuClick(object? sender, EventArgs e) =>
        _host?.Services.GetService<IAppLifetime>()?.Exit();

    // ---- Unhandled exceptions --------------------------------------------------------------------------------

    private void RegisterExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unhandled exception on the UI thread");
            e.Handled = true;
            ShowFriendlyError();
        };
    }

    private void ShowFriendlyError()
    {
        if (_errorDialogOpen || _host is null)
        {
            return;
        }

        _errorDialogOpen = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _host.Services.GetRequiredService<IDialogService>().ConfirmAsync(new ConfirmRequest(
                    "發生了一點問題",
                    ["Contexo 遇到沒預料到的狀況，已經記錄下來。", "您的原始檔案不會受到影響，可以繼續使用。"],
                    "知道了",
                    CancelText: null));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Could not show the error message");
            }
            finally
            {
                _errorDialogOpen = false;
            }
        });
    }
}
