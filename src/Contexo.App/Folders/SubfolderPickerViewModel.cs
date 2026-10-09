using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Services;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.Folders;

/// <summary>
/// One folder in the "choose sub folders" tree. Unticking a folder excludes its whole subtree; children follow the parent's state.
/// Children are read from disk when the node is first expanded.
/// </summary>
public sealed partial class SubfolderNodeViewModel : ViewModelBase
{
    private readonly SubfolderPickerViewModel? _picker;
    private bool _propagating;

    internal SubfolderNodeViewModel(SubfolderPickerViewModel? picker, SubfolderNodeViewModel? parent, string name, string relativePath, bool isChecked, bool isPlaceholder = false)
    {
        _picker = picker;
        Parent = parent;
        Name = name;
        RelativePath = relativePath;
        IsPlaceholder = isPlaceholder;
        IsChecked = isChecked;
    }

    internal SubfolderNodeViewModel? Parent { get; }

    public string Name { get; }

    /// <summary>Path below the watched folder, '/' separated.</summary>
    public string RelativePath { get; }

    /// <summary>The "loading" line shown under a node until its children are read.</summary>
    public bool IsPlaceholder { get; }

    public bool IsRealNode => !IsPlaceholder;

    public ObservableCollection<SubfolderNodeViewModel> Children { get; } = [];

    internal bool ChildrenLoaded { get; set; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>False while an ancestor is unticked: the node then simply follows its parent.</summary>
    public bool IsEnabled => !IsPlaceholder && (Parent is null || (Parent.IsChecked && Parent.IsEnabled));

    partial void OnIsCheckedChanged(bool value)
    {
        if (_propagating)
        {
            return;
        }

        foreach (var child in Children.Where(c => !c.IsPlaceholder))
        {
            child.SetFromParent(value);
        }

        RaiseEnabledChanged();
        _picker?.NotifyChanged();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !ChildrenLoaded && !IsPlaceholder)
        {
            _picker?.BeginLoadChildren(this);
        }
    }

    private void SetFromParent(bool value)
    {
        _propagating = true;
        IsChecked = value;
        _propagating = false;
        foreach (var child in Children.Where(c => !c.IsPlaceholder))
        {
            child.SetFromParent(value);
        }

        OnPropertyChanged(nameof(IsEnabled));
    }

    private void RaiseEnabledChanged()
    {
        foreach (var child in Children)
        {
            child.OnPropertyChanged(nameof(IsEnabled));
            child.RaiseEnabledChanged();
        }
    }
}

/// <summary>
/// The "choose sub folders" dialog. Saving stores only the topmost unticked folders (relative, '/' separated) and asks for a rescan.
/// Excluded paths deeper than the part of the tree the user opened are kept untouched.
/// </summary>
public sealed partial class SubfolderPickerViewModel : ViewModelBase, IDialogContent, IDisposable
{
    private readonly WatchedFolder _folder;
    private readonly IFolderTreeReader _tree;
    private readonly IKnowledgeStore _store;
    private readonly IIndexingService _indexing;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _initialExcluded;
    private readonly Dictionary<string, SubfolderNodeViewModel> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public SubfolderPickerViewModel(
        WatchedFolder folder,
        IFolderTreeReader tree,
        IKnowledgeStore store,
        IIndexingService indexing,
        ILogger? logger = null)
    {
        _folder = folder;
        _tree = tree;
        _store = store;
        _indexing = indexing;
        _logger = logger ?? NullLogger.Instance;
        _initialExcluded = new HashSet<string>(folder.ExcludedSubfolders.Select(NormalizeRelative), StringComparer.OrdinalIgnoreCase);
        _initialExcluded.Remove("");
    }

    public string Title => "選擇子資料夾 · " + _folder.DisplayName;

    public string Description => "取消勾選的資料夾，AI 不會讀取裡面的檔案。";

    public string FolderPath => PathRelations.ShortenMiddle(_folder.Path, 70);

    public string FullPath => _folder.Path;

    /// <summary>Top level folders; deeper levels are loaded when a node is expanded.</summary>
    public ObservableCollection<SubfolderNodeViewModel> Roots { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    /// <summary>True once the dialog saved a changed exclusion list.</summary>
    public bool Saved { get; private set; }

    public event EventHandler? CloseRequested;

    /// <summary>Completes when the first level (and any level being loaded) is on screen. Used by tests.</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the first level of folders.</summary>
    public Task LoadAsync()
    {
        LoadTask = LoadRootsAsync();
        return LoadTask;
    }

    private async Task LoadRootsAsync()
    {
        try
        {
            var names = await _tree.GetChildFoldersAsync(_folder.Path, _cts.Token);
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var name in names)
            {
                Roots.Add(CreateNode(null, name, true));
            }

            IsEmpty = Roots.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list sub folders of a watched folder");
            IsEmpty = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private SubfolderNodeViewModel CreateNode(SubfolderNodeViewModel? parent, string name, bool parentChecked)
    {
        var relative = parent is null ? name : parent.RelativePath + "/" + name;
        var isChecked = parentChecked && !_initialExcluded.Contains(relative);
        var node = new SubfolderNodeViewModel(this, parent, name, relative, isChecked);
        _nodes[relative] = node;
        node.Children.Add(new SubfolderNodeViewModel(null, node, "載入中…", relative + "/…", false, isPlaceholder: true));
        return node;
    }

    internal void BeginLoadChildren(SubfolderNodeViewModel node)
    {
        node.ChildrenLoaded = true;
        var task = LoadChildrenAsync(node);
        LoadTask = Task.WhenAll(LoadTask, task);
    }

    private async Task LoadChildrenAsync(SubfolderNodeViewModel node)
    {
        try
        {
            var absolute = _folder.Path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar + node.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var names = await _tree.GetChildFoldersAsync(absolute, _cts.Token);
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            node.Children.Clear();
            foreach (var name in names)
            {
                node.Children.Add(CreateNode(node, name, node.IsChecked));
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list sub folders");
            node.Children.Clear();
        }
    }

    internal void NotifyChanged() => OnPropertyChanged(nameof(HasChanges));

    /// <summary>True when the ticks differ from what is stored.</summary>
    public bool HasChanges => !SetEquals(ComputeExclusions(), _initialExcluded);

    /// <summary>
    /// The exclusion list that saving would store: topmost unticked folders, plus stored exclusions that lie in parts
    /// of the tree that were never opened (so nothing the user set earlier is lost).
    /// </summary>
    public IReadOnlyList<string> ComputeExclusions()
    {
        var result = new List<string>();
        foreach (var root in Roots)
        {
            Collect(root);
        }

        foreach (var stored in _initialExcluded)
        {
            // A stored path whose node was shown is decided by the node; otherwise it stays as it was.
            if (!_nodes.ContainsKey(stored))
            {
                result.Add(stored);
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !result.Any(other => other.Length < p.Length && p.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        void Collect(SubfolderNodeViewModel node)
        {
            if (!node.IsChecked)
            {
                result.Add(node.RelativePath);
                return;
            }

            foreach (var child in node.Children.Where(c => !c.IsPlaceholder))
            {
                Collect(child);
            }
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        if (IsSaving)
        {
            return;
        }

        IsSaving = true;
        try
        {
            var exclusions = ComputeExclusions();
            if (!SetEquals(exclusions, _initialExcluded))
            {
                await _store.SetFolderExclusionsAsync(_folder.Id, exclusions, CancellationToken.None);
                _indexing.RequestRescan(_folder.Id);
                Saved = true;
            }

            RequestClose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the folder exclusions");
            IsSaving = false;
            SaveError = "沒有辦法儲存，請稍後再試。";
        }
    }

    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    [RelayCommand]
    private void Cancel() => RequestClose();

    private void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    private static bool SetEquals(IReadOnlyList<string> list, HashSet<string> set) =>
        list.Count == set.Count && list.All(set.Contains);
}
