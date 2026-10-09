using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Services;
using Contexo.App.ViewModels;

namespace Contexo.App.Folders;

/// <summary>Dialog listing every file that could not be read, with the same one-button actions as the card on the folder page.</summary>
public sealed partial class FailedFilesDialogViewModel : ViewModelBase, IDialogContent
{
    internal FailedFilesDialogViewModel(ObservableCollection<FailedFileRowViewModel> rows)
    {
        Rows = rows;
    }

    public string Title => "無法讀取的檔案";

    /// <summary>The page's own collection, so the list shrinks while the dialog is open when a file is retried or skipped.</summary>
    public ObservableCollection<FailedFileRowViewModel> Rows { get; }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
