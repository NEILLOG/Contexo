using CommunityToolkit.Mvvm.ComponentModel;
using Contexo.App.Services;
using Contexo.App.ViewModels;

namespace Contexo.App.Shell;

/// <summary>
/// State of the overlay dialog layer of the main window. One dialog is visible at a time; further requests wait their turn.
/// Pure view-model logic so it can be tested without Avalonia; the Desktop <c>DialogService</c> is a thin adapter.
/// </summary>
public sealed partial class DialogHostViewModel : ViewModelBase, IDialogService
{
    private readonly IUiDispatcher _dispatcher;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private Action? _cancelCurrent;

    public DialogHostViewModel(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial object? Current { get; private set; }

    public bool IsOpen => Current is not null;

    public string Title => (Current as IDialogContent)?.Title ?? "";

    public async Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        var dialog = new ConfirmDialogViewModel(request);
        await ShowCoreAsync(dialog, dialog.Result, dialog.RequestCancel).ConfigureAwait(false);
        return await dialog.Result.ConfigureAwait(false);
    }

    public Task ShowAsync(object dialogViewModel)
    {
        ArgumentNullException.ThrowIfNull(dialogViewModel);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (dialogViewModel is IDialogContent content)
        {
            content.CloseRequested += (_, _) => closed.TrySetResult();
        }

        return ShowCoreAsync(dialogViewModel, closed.Task, () => closed.TrySetResult());
    }

    private async Task ShowCoreAsync(object dialog, Task completion, Action cancel)
    {
        await _queue.WaitAsync().ConfigureAwait(false);
        try
        {
            _cancelCurrent = cancel;
            _dispatcher.Post(() => Current = dialog);
            await completion.ConfigureAwait(false);
        }
        finally
        {
            _cancelCurrent = null;
            _dispatcher.Post(() => Current = null);
            _queue.Release();
        }
    }

    /// <summary>Esc pressed while a dialog is open. Returns true when a dialog handled it.</summary>
    public bool HandleEscape()
    {
        var cancel = _cancelCurrent;
        if (Current is null || cancel is null)
        {
            return false;
        }

        cancel();
        return true;
    }

    /// <summary>Enter pressed while a dialog is open. Returns true when it confirmed the dialog.</summary>
    public bool HandleEnter() => Current is ConfirmDialogViewModel confirm && confirm.TryConfirmWithEnter();
}
