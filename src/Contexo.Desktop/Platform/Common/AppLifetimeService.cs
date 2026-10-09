using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Contexo.App.Services;

namespace Contexo.Desktop.Platform.Common;

/// <summary>Shows the main window again (from the tray) and really exits the application.</summary>
public sealed class AppLifetimeService(MainWindowProvider windowProvider) : IAppLifetime
{
    /// <summary>True once <see cref="Exit"/> was called; the main window then closes instead of hiding to the tray.</summary>
    public bool IsExiting { get; private set; }

    public void ShowMainWindow() => Dispatcher.UIThread.Post(() =>
    {
        var window = windowProvider.Window;
        if (window is null)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    });

    public void Exit() => Dispatcher.UIThread.Post(() =>
    {
        IsExiting = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    });
}
