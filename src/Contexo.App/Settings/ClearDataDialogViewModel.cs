using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Services;
using Contexo.App.ViewModels;

namespace Contexo.App.Settings;

/// <summary>
/// "Clear all data" confirmation: warning lines, an "I understand" box that must be ticked, and the choice to rebuild right away.
/// Shown with <see cref="IDialogService.ShowAsync"/>; after it returns, <see cref="Confirmed"/> tells whether the user went ahead
/// (Esc closes the dialog without confirming).
/// </summary>
public sealed partial class ClearDataDialogViewModel : ViewModelBase, IDialogContent
{
    public string Title => "確定要清除全部資料嗎？";

    public IReadOnlyList<string> Lines { get; } =
    [
        "AI 將查不到任何資料，直到重新建立完成。",
        "重新建立可能需要一到數小時。",
        "你的原始檔案不會被刪除，資料夾清單和設定也會保留。",
    ];

    public string AcknowledgeText => "我了解需要重新建立，可能要數小時";

    public string RebuildText => "清除後立即重新建立";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool Acknowledged { get; set; }

    /// <summary>Start rebuilding as soon as the data is cleared. On by default.</summary>
    [ObservableProperty]
    public partial bool RebuildNow { get; set; } = true;

    public bool CanConfirm => Acknowledged;

    /// <summary>True only when the confirm button was used.</summary>
    public bool Confirmed { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanConfirm)
        {
            return;
        }

        Confirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
