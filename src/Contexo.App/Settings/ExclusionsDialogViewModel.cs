using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Services;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.App.Settings;

/// <summary>One row of the exclusion list.</summary>
public sealed class ExclusionItem(Exclusion exclusion)
{
    public Exclusion Exclusion { get; } = exclusion;

    public string Path => Exclusion.Path;

    public string TypeText => Exclusion.IsFolder ? "資料夾" : "檔案";

    public string AddedText => "加入時間 " + Exclusion.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// Lists the files and folders the user told Contexo not to read, with a "restore" button for each.
/// Restoring only removes the entry from Contexo's own list and asks for a re-scan; no user file is touched.
/// </summary>
public sealed partial class ExclusionsDialogViewModel : ViewModelBase, IDialogContent
{
    private readonly IKnowledgeStore _store;
    private readonly IIndexingService _indexing;
    private readonly ILogger _logger;

    public ExclusionsDialogViewModel(IKnowledgeStore store, IIndexingService indexing, ILogger logger)
    {
        _store = store;
        _indexing = indexing;
        _logger = logger;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Summary));
        };
    }

    public string Title => "排除的檔案與資料夾";

    public ObservableCollection<ExclusionItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public string Summary => IsEmpty ? "目前沒有排除任何項目。" : $"共 {Items.Count} 項。恢復後，Contexo 會重新讀取它們。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public event EventHandler? CloseRequested;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var exclusions = await _store.GetExclusionsAsync(cancellationToken);
            Items.Clear();
            foreach (var exclusion in exclusions.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                Items.Add(new ExclusionItem(exclusion));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the exclusion list");
            ErrorMessage = "無法讀取排除清單，請稍後再試。";
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(ExclusionItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _store.RemoveExclusionAsync(item.Exclusion.Id, CancellationToken.None);
            Items.Remove(item);
            ErrorMessage = null;
            _indexing.RequestRescan(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not restore an excluded item");
            ErrorMessage = "無法恢復這個項目，請稍後再試。";
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
