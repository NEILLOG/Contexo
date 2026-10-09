using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Contexo.App.Services;
using Microsoft.Extensions.Logging;

namespace Contexo.Desktop.Platform.Common;

/// <summary>Holds the main window so services created before it can still reach it.</summary>
public sealed class MainWindowProvider
{
    public Window? Window { get; set; }
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

public sealed class AvaloniaFolderPicker(MainWindowProvider windowProvider) : IFolderPicker, IFilePicker
{
    public async Task<string?> PickFolderAsync(string? initialPath)
    {
        var storage = windowProvider.Window?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
        {
            start = await storage.TryGetFolderFromPathAsync(initialPath);
        }

        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "選擇資料夾",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }

    public async Task<string?> PickSaveFolderAsync()
    {
        var storage = windowProvider.Window?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "選擇要存放的位置",
            AllowMultiple = false,
        });
        return result.Count == 0 ? null : result[0].TryGetLocalPath();
    }
}

public sealed class AvaloniaClipboardService(MainWindowProvider windowProvider, ILogger<AvaloniaClipboardService> logger) : IClipboardService
{
    public async Task<bool> TrySetTextAsync(string text)
    {
        try
        {
            var clipboard = windowProvider.Window?.Clipboard;
            if (clipboard is null)
            {
                return false;
            }

            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write to the clipboard");
            return false;
        }
    }
}
