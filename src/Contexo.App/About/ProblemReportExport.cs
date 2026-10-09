using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Services;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.About;

/// <summary>
/// The "export problem report" flow: options, choosing a folder, running the export in the background (cancellable) and the result box.
/// Shared by the About page and the start-up error screen.
/// </summary>
public sealed partial class ProblemReportExport : ViewModelBase
{
    private readonly IDiagnosticsExporter _exporter;
    private readonly IFilePicker _picker;
    private readonly IShellLauncher _launcher;
    private readonly ILogger _logger;
    private readonly string _desktopPath;
    private CancellationTokenSource? _cancellation;

    /// <param name="desktopPath">Tests pass a fake desktop; the default is the real one.</param>
    public ProblemReportExport(
        IDiagnosticsExporter exporter,
        IFilePicker picker,
        IShellLauncher launcher,
        ILogger? logger = null,
        string? desktopPath = null)
    {
        _exporter = exporter;
        _picker = picker;
        _launcher = launcher;
        _logger = logger ?? NullLogger.Instance;
        _desktopPath = desktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    // Options: the defaults of DiagnosticsOptions. "Version and system information" is always included and is not an option.

    [ObservableProperty]
    public partial bool IncludeSettings { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeRecentLogs { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeFailedFileList { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeFullPaths { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? ResultText { get; private set; }

    [ObservableProperty]
    public partial string? ResultZipPath { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool CanStart => !IsExporting;

    public bool HasResult => !string.IsNullOrEmpty(ResultText);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>The options as the user ticked them.</summary>
    public DiagnosticsOptions CurrentOptions => new()
    {
        IncludeSettings = IncludeSettings,
        IncludeRecentLogs = IncludeRecentLogs,
        LogDays = 7,
        IncludeFailedFileList = IncludeFailedFileList,
        IncludeFullPaths = IncludeFullPaths,
    };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task ExportAsync()
    {
        var folder = await _picker.PickSaveFolderAsync();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        ResultText = null;
        ResultZipPath = null;
        ErrorMessage = null;
        IsExporting = true;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        var options = CurrentOptions;
        try
        {
            // Packing reads log files and the database, so keep it off the UI thread.
            var result = await Task.Run(() => _exporter.ExportAsync(folder, options, token), token);
            ResultZipPath = result.ZipPath;
            ResultText = DescribeResult(result, folder);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消匯出，沒有產生檔案。";
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Problem report export: no permission to write");
            ErrorMessage = "沒有權限存放到這個位置，請換一個位置再試一次。";
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Problem report export: I/O error");
            ErrorMessage = "存檔時發生問題（可能是空間不足或位置無法使用），請換一個位置再試一次。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Problem report export failed");
            ErrorMessage = "匯出沒有成功，請再試一次。";
        }
        finally
        {
            IsExporting = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand]
    private void CancelExport() => _cancellation?.Cancel();

    [RelayCommand]
    private void OpenFolder()
    {
        if (!string.IsNullOrEmpty(ResultZipPath))
        {
            _launcher.RevealInFileManager(ResultZipPath);
        }
    }

    /// <summary>"已儲存到桌面：Contexo問題回報_20261008_0004.zip（1.2 MB）"</summary>
    private string DescribeResult(DiagnosticsResult result, string folder)
    {
        var place = IsSameFolder(folder, _desktopPath) ? "桌面" : $"「{LeafName(folder)}」資料夾";
        return $"已儲存到{place}：{Path.GetFileName(result.ZipPath)}（{FormatSize(result.SizeBytes)}）";
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => Math.Max(1, (bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KB",
    };

    private static bool IsSameFolder(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Normalize(a), Normalize(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string LeafName(string folder)
    {
        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? folder : name;
    }
}
