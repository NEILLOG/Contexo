using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Services;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.About;

/// <summary>About page: version card, problem report export card and the version history card.</summary>
public sealed partial class AboutViewModel : ViewModelBase, IPageLifecycle
{
    private readonly IDiagnosticsExporter? _exporter;
    private readonly IFilePicker? _filePicker;
    private readonly IShellLauncher? _launcher;
    private readonly IClipboardService? _clipboard;
    private readonly IEmbeddingService? _embedding;
    private readonly IAppPaths? _paths;
    private readonly ILogger _logger;
    private readonly AppVersionInfo _version;

    public AboutViewModel(
        IDiagnosticsExporter exporter,
        IFilePicker filePicker,
        IShellLauncher launcher,
        IClipboardService clipboard,
        IEmbeddingService embedding,
        IAppPaths paths,
        ILogger<AboutViewModel> logger)
        : this(exporter, filePicker, launcher, clipboard, embedding, paths, logger, AppVersion.Current, ChangelogParser.ReadEmbedded())
    {
    }

    /// <summary>
    /// Without services: the page shows the version only and cannot export. Used by the shell test helper and design-time previews;
    /// the application itself always uses the injected constructor.
    /// </summary>
    public AboutViewModel()
        : this(null, null, null, null, null, null, null, AppVersion.Current, null)
    {
    }

    /// <summary>Lets tests choose the version and the CHANGELOG.md text (null hides the version history card).</summary>
    internal AboutViewModel(
        IDiagnosticsExporter? exporter,
        IFilePicker? filePicker,
        IShellLauncher? launcher,
        IClipboardService? clipboard,
        IEmbeddingService? embedding,
        IAppPaths? paths,
        ILogger? logger,
        AppVersionInfo version,
        string? changelogText)
    {
        _exporter = exporter;
        _filePicker = filePicker;
        _launcher = launcher;
        _clipboard = clipboard;
        _embedding = embedding;
        _paths = paths;
        _logger = logger ?? NullLogger.Instance;
        _version = version;

        Changelog = ChangelogParser.Parse(changelogText);
        Export = CreateExportFlow();
        ModelText = DescribeModel();
    }

    public string Title => "關於與問題回報";

    public string ProductName => "文脈 Contexo";

    public string VersionText => _version.Version;

    /// <summary>"1.4.2+37.g3f2a9c1 · 2026-10-06"</summary>
    public string BuildText => _version.BuildDate is { } date
        ? $"{_version.InformationalVersion} · {date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
        : _version.InformationalVersion;

    [ObservableProperty]
    public partial string ModelText { get; private set; }

    [ObservableProperty]
    public partial string DatabaseVersionText { get; private set; } = "讀取中…";

    /// <summary>Set when the clipboard could not be used; the view then shows <see cref="VersionInfoText"/> in a selectable box.</summary>
    [ObservableProperty]
    public partial bool ShowVersionText { get; private set; }

    [ObservableProperty]
    public partial string? CopyStatus { get; private set; }

    /// <summary>The problem report flow; null when the page was created without services.</summary>
    public ProblemReportExport? Export { get; }

    public bool CanExport => Export is not null;

    public IReadOnlyList<ChangelogEntry> Changelog { get; }

    /// <summary>The version history card is hidden when CHANGELOG.md is missing or has no release in it.</summary>
    public bool HasChangelog => Changelog.Count > 0;

    /// <summary>Plain text that "複製版本資訊" puts on the clipboard.</summary>
    public string VersionInfoText =>
        $"{ProductName}\n版本：{VersionText}\n組建：{BuildText}\n本機模型：{ModelText}\n資料庫版本：{DatabaseVersionText}\n作業系統：{RuntimeInformation.OSDescription}";

    /// <summary>Creates a separate export flow with default options (used by the start-up error screen). Null without services.</summary>
    public ProblemReportExport? CreateExportFlow() =>
        _exporter is null || _filePicker is null || _launcher is null
            ? null
            : new ProblemReportExport(_exporter, _filePicker, _launcher, _logger);

    public void OnNavigatedTo() => _ = RefreshAsync();

    public void OnNavigatedFrom()
    {
    }

    /// <summary>Reads the parts of the version card that can change while the program runs (model availability, database version).</summary>
    public async Task RefreshAsync()
    {
        ModelText = DescribeModel();
        if (_paths is null)
        {
            DatabaseVersionText = "未知";
            return;
        }

        try
        {
            var version = await DatabaseVersionReader.TryReadAsync(_paths.DatabasePath, CancellationToken.None);
            DatabaseVersionText = version is { } v ? $"schema {v}" : "未知";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the database version");
            DatabaseVersionText = "未知";
        }
    }

    [RelayCommand]
    private async Task CopyVersionAsync()
    {
        var text = VersionInfoText;
        var copied = false;
        try
        {
            copied = _clipboard is not null && await _clipboard.TrySetTextAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copying the version information failed");
        }

        ShowVersionText = !copied;
        CopyStatus = copied ? "已複製" : "無法自動複製，請選取下面的文字再自行複製。";
    }

    private string DescribeModel()
    {
        if (_embedding is null)
        {
            return "未知";
        }

        try
        {
            // "bge-small-zh-v1.5/int8" -> "bge-small-zh-v1.5（int8）"
            var id = _embedding.ModelId;
            var slash = id.IndexOf('/');
            var name = slash > 0 ? $"{id[..slash]}（{id[(slash + 1)..]}）" : id;
            return _embedding.IsAvailable ? name : $"{name}，尚未安裝";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the embedding model information");
            return "未知";
        }
    }
}
