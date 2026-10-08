using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Contexo.Desktop;

public sealed partial class App : Application
{
    private IHost? _host;

    /// <summary>The application's services. Null until the desktop lifetime has started (and always null under the headless test lifetime).</summary>
    internal IServiceProvider? Services => _host?.Services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = BuildHost();
            desktop.Exit += (_, _) => _host.Dispose();
            desktop.MainWindow = new MainWindow();
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
        builder.Services.AddContexoCore();

        // T15 owns the rest of the start-up sequence (platform services, view models, starting the host).
        var host = builder.Build();
        host.Services.GetRequiredService<ILogger<App>>().LogInformation("Contexo started");
        return host;
    }
}
