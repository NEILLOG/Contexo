using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Services;
using Contexo.App.UserMessages;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.Folders;

/// <summary>A common location (or a folder the user added) offered in step 1 of the wizard.</summary>
public sealed partial class WizardLocationViewModel : ViewModelBase
{
    internal WizardLocationViewModel(string name, string path, bool isChecked, Action<WizardLocationViewModel> changed)
    {
        _changed = changed;
        Name = name;
        Path = path;
        IsChecked = isChecked;
    }

    private readonly Action<WizardLocationViewModel> _changed;

    public string Name { get; }

    public string Path { get; }

    public string DisplayPath => PathRelations.ShortenMiddle(Path, 60);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial string CountText { get; private set; } = "計算中…";

    /// <summary>Files found so far; null while counting or when counting failed.</summary>
    public int? Count { get; private set; }

    /// <summary>True when counting stopped at the limit ("超過 10,000 個").</summary>
    public bool IsTruncated { get; private set; }

    public bool IsCounting { get; private set; } = true;

    partial void OnIsCheckedChanged(bool value) => _changed(this);

    internal void SetCount(FileCount count)
    {
        Count = count.Count;
        IsTruncated = count.Truncated;
        IsCounting = false;
        CountText = count.Truncated
            ? $"超過 {count.Count:N0} 個檔案"
            : count.Count == 0 ? "沒有可讀取的檔案" : $"約 {count.Count:N0} 個檔案";
    }

    internal void SetCountUnknown()
    {
        Count = null;
        IsCounting = false;
        CountText = "";
    }
}

/// <summary>One check box of step 2.</summary>
public sealed partial class WizardCategoryViewModel : ViewModelBase
{
    internal WizardCategoryViewModel(FileCategory category, string name, string description, bool isChecked, bool isEnabled)
    {
        Category = category;
        Name = name;
        Description = description;
        IsChecked = isChecked;
        IsEnabled = isEnabled;
    }

    public FileCategory Category { get; }

    public string Name { get; }

    public string Description { get; }

    public bool IsEnabled { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>An installed AI program listed in step 3.</summary>
public sealed partial class WizardClientViewModel : ViewModelBase
{
    private readonly FirstRunViewModel _owner;

    internal WizardClientViewModel(FirstRunViewModel owner, AiClientStatus status)
    {
        _owner = owner;
        ClientId = status.ClientId;
        Name = status.DisplayName;
        Apply(status.State);
    }

    public string ClientId { get; }

    public string Name { get; }

    [ObservableProperty]
    public partial string StateText { get; private set; } = "";

    [ObservableProperty]
    public partial string ActionText { get; private set; } = "加入";

    [ObservableProperty]
    public partial bool CanAdd { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    public bool HasMessage => Message.Length > 0;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    private void Apply(ClientConnectionState state)
    {
        CanAdd = state is ClientConnectionState.NotAdded or ClientConnectionState.NeedsRepair;
        ActionText = state == ClientConnectionState.NeedsRepair ? "修復" : "加入";
        StateText = CanAdd ? ErrorText.ToText(state) : "已加入";
    }

    internal void MarkAdded()
    {
        CanAdd = false;
        StateText = "已加入";
        Message = $"請完全關閉 {Name} 後重新開啟";
    }

    internal void MarkFailed() => Message = "沒有加入成功，之後可以在「AI 軟體」頁面再試一次。";

    internal void SetBusy(bool busy) => IsBusy = busy;

    [RelayCommand]
    private Task Add() => _owner.AddClientAsync(this);
}

/// <summary>One entry of the "1 選資料夾 → 2 檔案類型 → 3 加入 AI 軟體" indicator.</summary>
public sealed partial class WizardStepViewModel : ObservableObject
{
    public WizardStepViewModel(int number, string text)
    {
        Number = number;
        Text = text;
        Label = $"{number} {text}";
    }

    public int Number { get; }

    public string Text { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial bool IsCurrent { get; internal set; }
}

/// <summary>
/// The first-run wizard: pick folders, pick file types, add AI programs. Finishing adds the folders, saves the settings
/// (which makes the shell leave the wizard and open the folder page) and asks the indexer to read everything.
/// </summary>
public sealed partial class FirstRunViewModel : ViewModelBase, IPageLifecycle, IDisposable
{
    /// <summary>Counting stops at this many files per folder.</summary>
    public const int CountLimit = 10_000;

    private static readonly FileCategory[] OfferedCategories = [FileCategory.Documents, FileCategory.Presentations, FileCategory.Spreadsheets, FileCategory.Pdf];

    private readonly IKnowledgeStore _store;
    private readonly ISettingsStore _settings;
    private readonly IIndexingService _indexing;
    private readonly IAiClientStatusService _clients;
    private readonly IFolderPicker _picker;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly IKnownFolders _known;
    private readonly IFolderFileCounter _counter;
    private readonly IFolderTreeReader _tree;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _countTasks = [];
    private readonly IReadOnlySet<string> _countExtensions = FileCategories.GetExtensions(OfferedCategories);
    private bool _initialized;

    public FirstRunViewModel(
        IKnowledgeStore store,
        ISettingsStore settings,
        IIndexingService indexing,
        IAiClientStatusService clients,
        IFolderPicker picker,
        IUiDispatcher dispatcher,
        ILogger<FirstRunViewModel>? logger = null,
        IKnownFolders? known = null,
        IFolderFileCounter? counter = null,
        IFolderTreeReader? tree = null)
    {
        _store = store;
        _settings = settings;
        _indexing = indexing;
        _clients = clients;
        _picker = picker;
        _dispatcher = dispatcher;
        _logger = logger ?? NullLogger<FirstRunViewModel>.Instance;
        _known = known ?? new SystemKnownFolders();
        _counter = counter ?? new FileSystemFolderFileCounter();
        _tree = tree ?? new FileSystemFolderTreeReader();

        Steps = [new WizardStepViewModel(1, "選資料夾"), new WizardStepViewModel(2, "檔案類型"), new WizardStepViewModel(3, "加入 AI 軟體")];
        Steps[0].IsCurrent = true;

        Categories =
        [
            new WizardCategoryViewModel(FileCategory.Documents, "文件", "Word、文字檔、網頁等", true, true),
            new WizardCategoryViewModel(FileCategory.Presentations, "簡報", "PowerPoint", true, true),
            new WizardCategoryViewModel(FileCategory.Spreadsheets, "試算表", "Excel、CSV", true, true),
            new WizardCategoryViewModel(FileCategory.Pdf, "PDF", "PDF 檔案", true, true),
            new WizardCategoryViewModel(FileCategory.Images, "圖片", "處理時間較長，之後的版本提供", false, false),
        ];
        foreach (var category in Categories)
        {
            category.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WizardCategoryViewModel.IsChecked))
                {
                    OnPropertyChanged(nameof(CanGoNext));
                }
            };
        }

        UpdateEstimate();
    }

    /// <summary>Used only when the shell is composed without services (shell tests); the instance is inert.</summary>
    internal FirstRunViewModel()
        : this(null!, null!, null!, null!, null!, null!)
    {
    }

    private bool IsInert => _store is null;

    public string Title => "歡迎使用文脈";

    public IReadOnlyList<WizardStepViewModel> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1))]
    [NotifyPropertyChangedFor(nameof(IsStep2))]
    [NotifyPropertyChangedFor(nameof(IsStep3))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    public partial int Step { get; private set; } = 1;

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    public bool IsStep3 => Step == 3;

    public bool CanGoBack => Step > 1 && !IsBusy;

    public bool CanGoNext => !IsBusy && (Step != 2 || Categories.Any(c => c.IsChecked));

    public string NextButtonText => Step == 3 ? "完成" : "下一步";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    public bool HasError => ErrorMessage.Length > 0;

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    // step 1
    public ObservableCollection<WizardLocationViewModel> Locations { get; } = [];

    [ObservableProperty]
    public partial string EstimateText { get; private set; } = "";

    // step 2
    public IReadOnlyList<WizardCategoryViewModel> Categories { get; }

    // step 3
    public ObservableCollection<WizardClientViewModel> Clients { get; } = [];

    [ObservableProperty]
    public partial bool HasClients { get; private set; }

    public bool HasNoClients => !HasClients;

    public string NoClientsText => "目前沒有偵測到支援的 AI 軟體，之後可以在「AI 軟體」頁面加入。";

    partial void OnHasClientsChanged(bool value) => OnPropertyChanged(nameof(HasNoClients));

    /// <summary>Completes when every folder count started so far has finished. Used by tests.</summary>
    internal Task CountingTask => Task.WhenAll(_countTasks.ToArray());

    // ---- Lifecycle -------------------------------------------------------------------------------------------

    public void OnNavigatedTo()
    {
        if (!IsInert)
        {
            _ = InitializeAsync();
        }
    }

    public void OnNavigatedFrom()
    {
    }

    /// <summary>Lists the common locations and starts counting their files. Runs once.</summary>
    public Task InitializeAsync()
    {
        if (_initialized || IsInert)
        {
            return Task.CompletedTask;
        }

        _initialized = true;
        foreach (var known in _known.GetKnownFolders())
        {
            var location = AddLocation(known.DisplayName, known.Path, known.SelectedByDefault);
            StartCounting(location);
        }

        UpdateEstimate();
        return Task.CompletedTask;
    }

    private WizardLocationViewModel AddLocation(string name, string path, bool isChecked)
    {
        var location = new WizardLocationViewModel(name, path, isChecked, _ => UpdateEstimate());
        Locations.Add(location);
        return location;
    }

    private void StartCounting(WizardLocationViewModel location) => _countTasks.Add(CountAsync(location));

    private async Task CountAsync(WizardLocationViewModel location)
    {
        try
        {
            var result = await _counter.CountAsync(location.Path, _countExtensions, CountLimit, _cts.Token);
            _dispatcher.Post(() =>
            {
                location.SetCount(result);
                UpdateEstimate();
            });
        }
        catch (OperationCanceledException)
        {
            // The wizard was closed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count the files of a folder");
            _dispatcher.Post(() =>
            {
                location.SetCountUnknown();
                UpdateEstimate();
            });
        }
    }

    // ---- Step 1: choosing folders ----------------------------------------------------------------------------

    [RelayCommand]
    private async Task PickOther()
    {
        var path = await _picker.PickFolderAsync(null);
        if (!string.IsNullOrWhiteSpace(path))
        {
            AddCustomFolder(path);
        }
    }

    /// <summary>Folders dropped on the window. Files are ignored.</summary>
    public void AddDroppedFolders(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && _tree.DirectoryExists(path))
            {
                AddCustomFolder(path);
            }
        }
    }

    /// <summary>Adds a folder the user chose or dropped, ticked. An already listed folder is just ticked.</summary>
    public void AddCustomFolder(string path)
    {
        var existing = Locations.FirstOrDefault(l => PathRelations.AreSame(l.Path, path));
        if (existing is not null)
        {
            existing.IsChecked = true;
            return;
        }

        var location = AddLocation(PathRelations.GetName(path), path, true);
        StartCounting(location);
        UpdateEstimate();
    }

    /// <summary>The checked folders that are really added: duplicates and folders inside another checked folder are left out.</summary>
    internal IReadOnlyList<string> SelectedFolderPaths()
    {
        var paths = Locations.Where(l => l.IsChecked).Select(l => l.Path).ToList();
        return paths
            .Where(p => !paths.Any(other => PathRelations.IsInside(other, p)))
            .DistinctBy(PathRelations.Normalize, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdateEstimate()
    {
        var selected = Locations.Where(l => l.IsChecked).ToList();
        var effective = selected.Where(l => !selected.Any(o => PathRelations.IsInside(o.Path, l.Path))).ToList();
        if (effective.Count == 0)
        {
            EstimateText = "還沒有選資料夾也沒關係，之後可以在「資料夾」頁面加入。";
            return;
        }

        var files = effective.Sum(l => l.Count ?? 0);
        var counting = effective.Any(l => l.IsCounting);
        var truncated = effective.Any(l => l.IsTruncated);
        if (counting && files == 0)
        {
            EstimateText = "正在計算檔案數，稍後會顯示預估時間。";
            return;
        }

        var estimate = TimeText.FirstBuildEstimate(files, atLeast: truncated);
        EstimateText = counting
            ? $"預估第一次建立需要{estimate}以上，期間可以照常使用電腦。"
            : $"預估第一次建立需要{estimate}，期間可以照常使用電腦。";
    }

    // ---- Navigation between steps ----------------------------------------------------------------------------

    [RelayCommand]
    private void Back()
    {
        if (Step > 1 && !IsBusy)
        {
            GoToStep(Step - 1);
        }
    }

    [RelayCommand]
    private async Task Next()
    {
        if (!CanGoNext)
        {
            return;
        }

        switch (Step)
        {
            case 1:
                GoToStep(2);
                break;
            case 2:
                GoToStep(3);
                await LoadClientsAsync();
                break;
            default:
                await FinishAsync();
                break;
        }
    }

    private void GoToStep(int step)
    {
        ErrorMessage = "";
        Step = step;
        foreach (var item in Steps)
        {
            item.IsCurrent = item.Number == step;
        }
    }

    // ---- Step 3: AI programs ---------------------------------------------------------------------------------

    /// <summary>Lists the AI programs found on this computer.</summary>
    public async Task LoadClientsAsync()
    {
        IReadOnlyList<AiClientStatus> statuses;
        try
        {
            statuses = await _clients.GetStatusesAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the AI program list");
            statuses = [];
        }

        Clients.Clear();
        foreach (var status in statuses.Where(s => s.State != ClientConnectionState.NotInstalled))
        {
            Clients.Add(new WizardClientViewModel(this, status));
        }

        HasClients = Clients.Count > 0;
    }

    internal async Task AddClientAsync(WizardClientViewModel row)
    {
        var integration = _clients.Integrations.FirstOrDefault(i => i.ClientId == row.ClientId);
        if (integration is null || !row.CanAdd)
        {
            return;
        }

        row.SetBusy(true);
        try
        {
            var launch = _clients.CurrentLaunch;
            await Task.Run(() => integration.AddOrRepair(launch));
            row.MarkAdded();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not add Contexo to an AI program");
            row.MarkFailed();
        }
        finally
        {
            row.SetBusy(false);
        }
    }

    // ---- Finishing -------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds the folders, saves the settings (file types and <c>FirstRunCompleted</c>), then asks the indexer to read everything.
    /// The shell leaves the wizard when the settings are saved. If a folder cannot be added the wizard stays open.
    /// </summary>
    public async Task FinishAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = "";
        try
        {
            var failed = new List<string>();
            foreach (var path in SelectedFolderPaths())
            {
                try
                {
                    if (!_tree.DirectoryExists(path))
                    {
                        failed.Add(PathRelations.GetName(path));
                        continue;
                    }

                    await _store.AddFolderAsync(path, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not add a folder in the first-run wizard");
                    failed.Add(PathRelations.GetName(path));
                }
            }

            if (failed.Count > 0)
            {
                ErrorMessage = $"有 {failed.Count} 個資料夾沒辦法加入（{string.Join("、", failed.Select(n => $"『{n}』"))}）。請取消勾選這些資料夾，或稍後再試。";
                return;
            }

            var current = _settings.Current;
            var enabled = Categories.Where(c => c.IsChecked && c.IsEnabled).Select(c => c.Category).ToList();
            if (current.EnabledCategories.Contains(FileCategory.Email))
            {
                enabled.Add(FileCategory.Email);
            }

            await _settings.SaveAsync(current with { EnabledCategories = enabled, FirstRunCompleted = true }, CancellationToken.None);
            _indexing.RequestRescan(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not finish the first-run wizard");
            ErrorMessage = "沒辦法完成設定，請稍後再試一次。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
