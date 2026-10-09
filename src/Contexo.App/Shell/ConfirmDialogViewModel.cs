using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Services;
using Contexo.App.ViewModels;

namespace Contexo.App.Shell;

/// <summary>Content of the dialog opened by <see cref="IDialogService.ConfirmAsync"/>.</summary>
public sealed partial class ConfirmDialogViewModel : ViewModelBase, IDialogContent
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmDialogViewModel(ConfirmRequest request)
    {
        Request = request;
    }

    public ConfirmRequest Request { get; }

    public string Title => Request.Title;

    public IReadOnlyList<string> Lines => Request.Lines;

    public bool HasLines => Request.Lines.Count > 0;

    public string ConfirmText => Request.ConfirmText;

    public string? CancelText => Request.CancelText;

    public bool HasCancel => Request.CancelText is not null;

    public bool IsDestructive => Request.IsDestructive;

    public string? AcknowledgeText => Request.AcknowledgeText;

    public bool RequiresAcknowledge => Request.AcknowledgeText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool Acknowledged { get; set; }

    /// <summary>False until the "I understand" box is ticked, when one is required.</summary>
    public bool CanConfirm => !RequiresAcknowledge || Acknowledged;

    public Task<bool> Result => _result.Task;

    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Complete(true);

    [RelayCommand]
    private void Cancel() => Complete(false);

    /// <summary>Esc: cancels the dialog.</summary>
    public void RequestCancel() => Complete(false);

    /// <summary>Enter: confirms, unless the action is dangerous or still needs the acknowledgement.</summary>
    public bool TryConfirmWithEnter()
    {
        if (IsDestructive || !CanConfirm)
        {
            return false;
        }

        Complete(true);
        return true;
    }

    private void Complete(bool confirmed)
    {
        if (_result.TrySetResult(confirmed))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
