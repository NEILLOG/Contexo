using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Services;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.Settings;

/// <summary>One pill of the "file types to read" card.</summary>
public sealed class CategoryOption : ObservableObject
{
    private readonly Action<CategoryOption, bool>? _toggled;
    private bool _isChecked;
    private bool _silent;

    internal CategoryOption(FileCategory category, string label, string? hint, bool isAvailable, Action<CategoryOption, bool>? toggled)
    {
        Category = category;
        Label = label;
        Hint = hint;
        IsAvailable = isAvailable;
        _toggled = toggled;
    }

    public FileCategory Category { get; }

    public string Label { get; }

    /// <summary>Small note next to the label, for example "Word、文字檔".</summary>
    public string? Hint { get; }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    /// <summary>False for types that a later version provides; the pill is shown disabled.</summary>
    public bool IsAvailable { get; }

    /// <summary>Set by the user through the check box. The view model may put the box back (cancelled or not allowed).</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value) && !_silent)
            {
                _toggled?.Invoke(this, value);
            }
        }
    }

    /// <summary>Changes the box without raising the user-change handler.</summary>
    internal void SetSilently(bool value)
    {
        _silent = true;
        try
        {
            IsChecked = value;
        }
        finally
        {
            _silent = false;
        }
    }
}

/// <summary>
/// Settings page. Every change is saved at once (no save button) through <see cref="ISettingsStore"/>; the theme and text size
/// listeners of the shell and the indexing service react to <see cref="ISettingsStore.Changed"/> by themselves.
/// Saves run one after another, each applied to the latest settings, so quick successive changes end with the last one.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IPageLifecycle
{
    private static readonly FileCategory[] AvailableCategories =
        [FileCategory.Documents, FileCategory.Presentations, FileCategory.Spreadsheets, FileCategory.Pdf];

    private static readonly (string Label, int? Mb)[] FileSizeChoices =
        [("20 MB", 20), ("50 MB", 50), ("100 MB", 100), ("不限制", null)];

    private readonly ISettingsStore _settings = null!;
    private readonly IIndexingService _indexing = null!;
    private readonly IKnowledgeStore _store = null!;
    private readonly IAppPaths _paths = null!;
    private readonly IDialogService _dialogs = null!;
    private readonly IStartupRegistration _startup = null!;
    private readonly IShellLauncher _launcher = null!;
    private readonly IClipboardService _clipboard = null!;
    private readonly IAiClientStatusService _clients = null!;
    private readonly IUiDispatcher _dispatcher = null!;
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly bool _wired;

    private readonly object _gate = new();
    private readonly (string Label, int? Mb)[] _fileSizes;
    private Task _saveTail = Task.CompletedTask;
    private Task _categoryTail = Task.CompletedTask;
    private int _pendingSaves;
    private bool _applying;
    private AppSettings _lastSeen;

    public SettingsViewModel(
        ISettingsStore settings,
        IIndexingService indexing,
        IKnowledgeStore store,
        IAppPaths paths,
        IDialogService dialogs,
        IStartupRegistration startup,
        IShellLauncher launcher,
        IClipboardService clipboard,
        IAiClientStatusService clients,
        IUiDispatcher dispatcher,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _indexing = indexing;
        _store = store;
        _paths = paths;
        _dialogs = dialogs;
        _startup = startup;
        _launcher = launcher;
        _clipboard = clipboard;
        _clients = clients;
        _dispatcher = dispatcher;
        _logger = logger;
        _wired = true;

        var current = settings.Current;
        _lastSeen = current;
        _fileSizes = BuildFileSizeChoices(current.MaxFileSizeMb);
        FileSizeLabels = _fileSizes.Select(c => c.Label).ToList();
        Categories = BuildCategories();
        ClientNames = clients.Integrations.Select(i => i.DisplayName).ToList();
        SelectedClientIndex = ClientNames.Count > 0 ? 0 : -1;
        DataDirectory = paths.DataDirectory;
        Apply(current);

        SyncStartupRegistration(current);
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>
    /// A page with no services behind it, so other pages' tests can build the shell without faking everything.
    /// It shows the default settings and changes nothing.
    /// </summary>
    internal SettingsViewModel()
    {
        var current = new AppSettings();
        _lastSeen = current;
        _fileSizes = BuildFileSizeChoices(current.MaxFileSizeMb);
        FileSizeLabels = _fileSizes.Select(c => c.Label).ToList();
        Categories = BuildCategories();
        ClientNames = [];
        SelectedClientIndex = -1;
        Apply(current);
    }

    public string Title => "設定";

    // --- Appearance ---------------------------------------------------------------------------------------------

    /// <summary>0 follow system, 1 light, 2 dark.</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    /// <summary>0 standard, 1 large, 2 extra large.</summary>
    [ObservableProperty]
    public partial int FontScaleIndex { get; set; }

    partial void OnThemeIndexChanged(int value)
    {
        if (!_applying && Enum.IsDefined(typeof(ThemePreference), value))
        {
            Enqueue(s => s with { Theme = (ThemePreference)value });
        }
    }

    partial void OnFontScaleIndexChanged(int value)
    {
        if (!_applying && Enum.IsDefined(typeof(FontScale), value))
        {
            Enqueue(s => s with { FontScale = (FontScale)value });
        }
    }

    // --- File types ---------------------------------------------------------------------------------------------

    public IReadOnlyList<CategoryOption> Categories { get; }

    // --- Behaviour ----------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool FullSpeedOnlyWhenIdle { get; set; }

    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    public IReadOnlyList<string> FileSizeLabels { get; }

    [ObservableProperty]
    public partial int MaxFileSizeIndex { get; set; }

    partial void OnFullSpeedOnlyWhenIdleChanged(bool value)
    {
        if (!_applying)
        {
            Enqueue(s => s with { FullSpeedOnlyWhenIdle = value });
        }
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (!_applying)
        {
            Enqueue(s => s with { MinimizeToTray = value });
        }
    }

    partial void OnMaxFileSizeIndexChanged(int value)
    {
        if (!_applying && value >= 0 && value < _fileSizes.Length)
        {
            var mb = _fileSizes[value].Mb;
            Enqueue(s => s with { MaxFileSizeMb = mb });
        }
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        // The start-up entry is changed first; when the system refuses, the setting stays as it was.
        Enqueue(s =>
        {
            try
            {
                _startup.SetEnabled(value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not change the start-up entry");
                ShowError("無法設定開機自動啟動，可能是這台電腦的安全設定不允許。");
                return null;
            }

            return s with { LaunchAtStartup = value };
        });
    }

    // --- Advanced -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsAdvancedExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdvancedEnabled))]
    public partial bool IsClearing { get; private set; }

    /// <summary>False while data is being cleared, so the buttons cannot be pressed twice.</summary>
    public bool IsAdvancedEnabled => !IsClearing;

    [ObservableProperty]
    public partial string DataSizeText { get; private set; } = "—";

    public string DataDirectory { get; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExclusionsButtonText))]
    public partial int ExclusionCount { get; private set; }

    public string ExclusionsButtonText => $"管理（{ExclusionCount}）";

    public IReadOnlyList<string> ClientNames { get; }

    public bool HasClients => ClientNames.Count > 0;

    [ObservableProperty]
    public partial int SelectedClientIndex { get; set; }

    /// <summary>Arrow at the right of the "進階" heading.</summary>
    public string AdvancedGlyph => IsAdvancedExpanded ? "▲" : "▼";

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    partial void OnIsAdvancedExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(AdvancedGlyph));
        if (value)
        {
            _ = RefreshAdvancedAsync();
        }
    }

    // --- Messages -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfoMessage))]
    public partial string? InfoMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasInfoMessage => !string.IsNullOrEmpty(InfoMessage);

    public bool HasErrorMessage => !string.IsNullOrEmpty(ErrorMessage);

    // --- Page life cycle ----------------------------------------------------------------------------------------

    public void OnNavigatedTo()
    {
        if (_wired && IsAdvancedExpanded)
        {
            _ = RefreshAdvancedAsync();
        }
    }

    public void OnNavigatedFrom()
    {
    }

    // --- Commands -----------------------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (_wired)
        {
            _launcher.OpenFolder(_paths.DataDirectory);
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (_wired)
        {
            _launcher.OpenFolder(_paths.LogsDirectory);
        }
    }

    [RelayCommand]
    private async Task ManageExclusionsAsync()
    {
        if (!_wired)
        {
            return;
        }

        var dialog = new ExclusionsDialogViewModel(_store, _indexing, _logger);
        await dialog.LoadAsync();
        await _dialogs.ShowAsync(dialog);
        await RefreshAdvancedAsync();
    }

    [RelayCommand]
    private async Task CopyClientSnippetAsync()
    {
        if (!_wired || SelectedClientIndex < 0 || SelectedClientIndex >= _clients.Integrations.Count)
        {
            return;
        }

        ClearMessages();
        var integration = _clients.Integrations[SelectedClientIndex];
        try
        {
            var snippet = integration.BuildManualSnippet(_clients.CurrentLaunch);
            if (await _clipboard.TrySetTextAsync(snippet))
            {
                InfoMessage = $"已複製「{integration.DisplayName}」的設定內容，可以貼給負責的人。";
            }
            else
            {
                ErrorMessage = "無法複製，請稍後再試。";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not build the manual setup text for {Client}", integration.ClientId);
            ErrorMessage = "無法產生設定內容，請稍後再試。";
        }
    }

    [RelayCommand]
    private async Task ClearAllDataAsync()
    {
        if (!_wired || IsClearing)
        {
            return;
        }

        ClearMessages();
        var dialog = new ClearDataDialogViewModel();
        await _dialogs.ShowAsync(dialog);
        if (!dialog.Confirmed)
        {
            return;
        }

        IsClearing = true;
        var rebuild = dialog.RebuildNow;
        try
        {
            _indexing.Pause();
            long before = 0;
            try
            {
                before = (await _store.GetStatisticsAsync(CancellationToken.None)).DatabaseBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read the data size before clearing");
            }

            await _store.ClearIndexedDataAsync(CancellationToken.None);

            if (rebuild)
            {
                _indexing.RequestRescan(null);
            }

            long after = 0;
            try
            {
                after = (await _store.GetStatisticsAsync(CancellationToken.None)).DatabaseBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read the data size after clearing");
            }

            var cleared = Math.Max(0, before - after);
            var what = cleared > 0 ? $"已清除 {DataSizeFormatter.Format(cleared)} 資料" : "已清除全部資料";
            InfoMessage = rebuild ? what + "，正在重新建立。" : what + "。";
            DataSizeText = DataSizeFormatter.Format(after);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Clearing the data failed");
            ErrorMessage = "清除資料時發生問題，資料可能沒有清除完整。請稍後再試一次。";
        }
        finally
        {
            _indexing.Resume();
            IsClearing = false;
        }

        await RefreshAdvancedAsync();
    }

    // --- Categories ---------------------------------------------------------------------------------------------

    private List<CategoryOption> BuildCategories() =>
    [
        new(FileCategory.Documents, "文件", "Word、文字檔", true, OnCategoryToggled),
        new(FileCategory.Presentations, "簡報", null, true, OnCategoryToggled),
        new(FileCategory.Spreadsheets, "試算表", null, true, OnCategoryToggled),
        new(FileCategory.Pdf, "PDF", null, true, OnCategoryToggled),
        new(FileCategory.Email, "郵件", "之後的版本提供", false, OnCategoryToggled),
        new(FileCategory.Images, "圖片", "之後的版本提供（處理時間較長）", false, OnCategoryToggled),
    ];

    private void OnCategoryToggled(CategoryOption option, bool isChecked)
    {
        if (!_wired)
        {
            return;
        }

        _categoryTail = SetCategoryCoreAsync(_categoryTail, option, isChecked);
    }

    private async Task SetCategoryCoreAsync(Task previous, CategoryOption option, bool isChecked)
    {
        try
        {
            await previous;
        }
        catch
        {
            // The earlier toggle reported its own problem.
        }

        try
        {
            await SetCategoryAsync(option, isChecked);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not change a file type");
            option.SetSilently(!isChecked);
            ErrorMessage = "無法變更檔案類型，請稍後再試。";
        }
    }

    /// <summary>Turns a file type on or off. Turning one off asks first, and the last remaining type cannot be turned off.</summary>
    internal async Task SetCategoryAsync(CategoryOption option, bool isChecked)
    {
        if (!option.IsAvailable)
        {
            option.SetSilently(false);
            return;
        }

        ClearMessages();
        var enabled = _settings.Current.EnabledCategories;
        if (isChecked)
        {
            if (!enabled.Contains(option.Category))
            {
                await EnqueueAndWait(s => s with { EnabledCategories = Ordered(s.EnabledCategories.Append(option.Category)) });
            }

            return;
        }

        var remaining = AvailableCategories.Count(c => c != option.Category && enabled.Contains(c));
        if (remaining == 0)
        {
            option.SetSilently(true);
            ErrorMessage = "至少要保留一種檔案類型。";
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
            $"不再讀取「{option.Label}」嗎？",
            ["取消後，AI 將查不到這類檔案的內容。", "你的原始檔案不受影響。"],
            "不再讀取"));
        if (!confirmed)
        {
            option.SetSilently(true);
            return;
        }

        await EnqueueAndWait(s => s with { EnabledCategories = Ordered(s.EnabledCategories.Where(c => c != option.Category)) });
    }

    private static IReadOnlyList<FileCategory> Ordered(IEnumerable<FileCategory> categories) =>
        categories.Distinct().OrderBy(c => (int)c).ToList();

    // --- Saving -------------------------------------------------------------------------------------------------

    /// <summary>Queues a change. It is applied to the latest settings when its turn comes; returning null skips the save.</summary>
    private void Enqueue(Func<AppSettings, AppSettings?> change) => _ = EnqueueAndWait(change);

    private Task EnqueueAndWait(Func<AppSettings, AppSettings?> change)
    {
        if (!_wired)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            _pendingSaves++;
            _saveTail = RunAfterAsync(_saveTail, change);
            return _saveTail;
        }
    }

    private async Task RunAfterAsync(Task previous, Func<AppSettings, AppSettings?> change)
    {
        // Leave the calling (UI) thread: the change may touch the registry or files.
        await Task.Yield();
        try
        {
            await previous.ConfigureAwait(false);
            var current = _settings.Current;
            var next = change(current);
            if (next is not null && !next.Equals(current))
            {
                await _settings.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not save the settings");
            ShowError("無法儲存設定，請稍後再試。");
        }
        finally
        {
            bool idle;
            lock (_gate)
            {
                _pendingSaves--;
                idle = _pendingSaves == 0;
            }

            if (idle)
            {
                // Show what is really stored (also puts back a switch whose change was refused).
                _dispatcher.Post(() =>
                {
                    bool stillIdle;
                    lock (_gate)
                    {
                        stillIdle = _pendingSaves == 0;
                    }

                    if (stillIdle)
                    {
                        Apply(_settings.Current);
                    }
                });
            }
        }
    }

    /// <summary>Completes when every queued change has been handled. Used by tests.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task save;
            Task category;
            lock (_gate)
            {
                save = _saveTail;
                category = _categoryTail;
            }

            await Task.WhenAll(save, category);
            lock (_gate)
            {
                if (ReferenceEquals(save, _saveTail) && ReferenceEquals(category, _categoryTail))
                {
                    return;
                }
            }
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        bool busy;
        lock (_gate)
        {
            busy = _pendingSaves > 0;
        }

        // While our own changes are still being saved, the screen already shows the newest choice.
        if (!busy)
        {
            _dispatcher.Post(() => Apply(settings));
        }

        if (settings.FirstRunCompleted != _lastSeen.FirstRunCompleted || settings.LaunchAtStartup != _lastSeen.LaunchAtStartup)
        {
            SyncStartupRegistration(settings);
        }

        _lastSeen = settings;
    }

    /// <summary>Copies stored settings to the screen without saving them again.</summary>
    private void Apply(AppSettings settings)
    {
        var wasApplying = _applying;
        _applying = true;
        try
        {
            ThemeIndex = (int)settings.Theme;
            FontScaleIndex = (int)settings.FontScale;
            FullSpeedOnlyWhenIdle = settings.FullSpeedOnlyWhenIdle;
            LaunchAtStartup = settings.LaunchAtStartup;
            MinimizeToTray = settings.MinimizeToTray;
            MaxFileSizeIndex = IndexOfFileSize(settings.MaxFileSizeMb);
            foreach (var option in Categories)
            {
                option.SetSilently(option.IsAvailable && settings.EnabledCategories.Contains(option.Category));
            }
        }
        finally
        {
            _applying = wasApplying;
        }
    }

    private int IndexOfFileSize(int? mb)
    {
        for (var i = 0; i < _fileSizes.Length; i++)
        {
            if (_fileSizes[i].Mb == mb)
            {
                return i;
            }
        }

        return _fileSizes.Length - 1;
    }

    private static (string Label, int? Mb)[] BuildFileSizeChoices(int? current)
    {
        if (current is null || FileSizeChoices.Any(c => c.Mb == current))
        {
            return FileSizeChoices;
        }

        // A value written by hand into the settings file: keep it selectable, in order.
        return FileSizeChoices
            .Append((Label: $"{current} MB", Mb: current))
            .OrderBy(c => c.Mb ?? int.MaxValue)
            .ToArray();
    }

    // --- Start-up entry -----------------------------------------------------------------------------------------

    /// <summary>
    /// Makes the system's start-up entry match the setting once the first-run wizard is done. This also repairs an entry that
    /// points at an old location after the program was moved or updated.
    /// </summary>
    private void SyncStartupRegistration(AppSettings settings)
    {
        if (!settings.FirstRunCompleted)
        {
            return;
        }

        try
        {
            if (_startup.IsEnabled != settings.LaunchAtStartup)
            {
                _startup.SetEnabled(settings.LaunchAtStartup);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not bring the start-up entry in line with the setting");
        }
    }

    // --- Advanced data ------------------------------------------------------------------------------------------

    /// <summary>Reads the data size and the number of exclusions.</summary>
    internal async Task RefreshAdvancedAsync()
    {
        if (!_wired)
        {
            return;
        }

        try
        {
            var statistics = await _store.GetStatisticsAsync(CancellationToken.None);
            var exclusions = await _store.GetExclusionsAsync(CancellationToken.None);
            DataSizeText = DataSizeFormatter.Format(statistics.DatabaseBytes);
            ExclusionCount = exclusions.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the data size");
        }
    }

    // --- Messages -----------------------------------------------------------------------------------------------

    private void ClearMessages()
    {
        InfoMessage = null;
        ErrorMessage = null;
    }

    private void ShowError(string message) => _dispatcher.Post(() =>
    {
        InfoMessage = null;
        ErrorMessage = message;
    });
}
