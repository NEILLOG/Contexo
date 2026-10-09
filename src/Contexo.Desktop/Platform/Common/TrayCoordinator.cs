using Avalonia;
using Avalonia.Controls;
using Contexo.App.Services;
using Contexo.App.Shell;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.Desktop.Platform.Common;

/// <summary>
/// Close button behaviour: with "MinimizeToTray" the window is hidden instead of closed (first time with an in-window hint),
/// and the shell is told when the window is hidden or shown so pages can pause their updates.
/// </summary>
public sealed class TrayCoordinator
{
    private const string HintShownMetaKey = "ui.tray_hint_shown";

    private readonly AppLifetimeService _lifetime;
    private readonly ShellViewModel _shell;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly IKnowledgeStore _store;
    private readonly ILogger<TrayCoordinator> _logger;
    private bool _hiding;

    public TrayCoordinator(
        AppLifetimeService lifetime,
        ShellViewModel shell,
        ISettingsStore settings,
        IDialogService dialogs,
        IKnowledgeStore store,
        ILogger<TrayCoordinator> logger)
    {
        _lifetime = lifetime;
        _shell = shell;
        _settings = settings;
        _dialogs = dialogs;
        _store = store;
        _logger = logger;
    }

    public void Attach(Window window)
    {
        window.Closing += OnClosing;
        window.Closed += (_, _) =>
        {
            if (!_lifetime.IsExiting)
            {
                _lifetime.Exit();
            }
        };
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty)
            {
                return;
            }

            if (window.IsVisible)
            {
                _shell.OnWindowShown();
            }
            else
            {
                _shell.OnWindowHidden();
            }
        };
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Only the user's own close button hides to the tray; Cmd+Q, log off and Exit really close.
        if (_lifetime.IsExiting || e.CloseReason != WindowCloseReason.WindowClosing || !_settings.Current.MinimizeToTray)
        {
            return;
        }

        e.Cancel = true;
        if (sender is Window window && !_hiding)
        {
            _ = HideToTrayAsync(window);
        }
    }

    private async Task HideToTrayAsync(Window window)
    {
        _hiding = true;
        try
        {
            if (!await HintAlreadyShownAsync())
            {
                var where = OperatingSystem.IsMacOS() ? "上方選單列" : "右下角";
                await _dialogs.ConfirmAsync(new ConfirmRequest(
                    "Contexo 會在背景繼續執行",
                    [$"可以從{where}的圖示再打開 Contexo。", "如果想完全關閉，請在圖示的選單選擇「結束」。"],
                    "知道了",
                    CancelText: null));
                await MarkHintShownAsync();
            }

            window.Hide();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not hide the window to the tray");
        }
        finally
        {
            _hiding = false;
        }
    }

    private async Task<bool> HintAlreadyShownAsync()
    {
        try
        {
            return await _store.GetMetaAsync(HintShownMetaKey, CancellationToken.None) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the tray hint flag");
            return false;
        }
    }

    private async Task MarkHintShownAsync()
    {
        try
        {
            await _store.SetMetaAsync(HintShownMetaKey, "1", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the tray hint flag");
        }
    }
}
