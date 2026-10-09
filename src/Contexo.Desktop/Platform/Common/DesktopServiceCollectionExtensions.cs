using Contexo.App.About;
using Contexo.App.AiClients;
using Contexo.App.Folders;
using Contexo.App.Search;
using Contexo.App.Services;
using Contexo.App.Settings;
using Contexo.App.Shell;
using Contexo.Core.Abstractions;
using Contexo.Desktop.Platform.Mac;
using Contexo.Desktop.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Contexo.Desktop.Platform.Common;

public static class DesktopServiceCollectionExtensions
{
    /// <summary>
    /// Registers platform services, shared UI services, the shell and every page view model.
    /// Call it <b>before</b> <c>AddContexoCore()</c> so the platform <see cref="IUserActivityMonitor"/> wins over Core's default.
    /// Later tasks create their dialog view models themselves and do not register them here.
    /// </summary>
    public static IServiceCollection AddContexoDesktop(this IServiceCollection services)
    {
        // Platform specific: Windows is the product; everything else (macOS for development, Linux in CI) uses the Mac versions.
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IUserActivityMonitor, WindowsUserActivityMonitor>();
            services.AddSingleton<IShellLauncher, WindowsShellLauncher>();
            services.AddSingleton<IStartupRegistration, WindowsStartupRegistration>();
        }
        else
        {
            services.AddSingleton<IUserActivityMonitor, MacUserActivityMonitor>();
            services.AddSingleton<IShellLauncher, MacShellLauncher>();
            services.AddSingleton<IStartupRegistration, MacStartupRegistration>();
        }

        // Cross-platform Avalonia services
        services.AddSingleton<MainWindowProvider>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<AvaloniaFolderPicker>();
        services.AddSingleton<IFolderPicker>(sp => sp.GetRequiredService<AvaloniaFolderPicker>());
        services.AddSingleton<IFilePicker>(sp => sp.GetRequiredService<AvaloniaFolderPicker>());
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<AppLifetimeService>();
        services.AddSingleton<IAppLifetime>(sp => sp.GetRequiredService<AppLifetimeService>());
        services.AddSingleton<TrayCoordinator>();

        // Shell and shared UI state
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DialogHostViewModel>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogHostViewModel>());
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<ShellViewModel>();

        // Pages (empty in T15; T16-T20 fill them in)
        services.AddSingleton<FoldersViewModel>();
        services.AddSingleton<FirstRunViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<AiClientsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();

        return services;
    }
}
