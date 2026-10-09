namespace Contexo.App.Services;

/// <summary>
/// Describes a confirmation dialog shown as an overlay inside the main window.
/// </summary>
/// <param name="Title">Dialog title.</param>
/// <param name="Lines">Explanation shown as a bullet list. May be empty.</param>
/// <param name="ConfirmText">Text of the confirm button.</param>
/// <param name="IsDestructive">True for dangerous actions: the confirm button is styled as danger and is never the default (Enter) button.</param>
/// <param name="AcknowledgeText">When set, the user must tick a check box with this text before the confirm button is enabled.</param>
/// <param name="CancelText">Text of the cancel button; null hides it (information-only dialogs).</param>
public sealed record ConfirmRequest(
    string Title,
    IReadOnlyList<string> Lines,
    string ConfirmText,
    bool IsDestructive = false,
    string? AcknowledgeText = null,
    string? CancelText = "取消");

/// <summary>Implemented by view models shown with <see cref="IDialogService.ShowAsync"/>.</summary>
public interface IDialogContent
{
    string Title { get; }

    /// <summary>Raised by the view model when the dialog should close.</summary>
    event EventHandler? CloseRequested;
}

/// <summary>Overlay dialogs inside the main window (never system dialogs).</summary>
public interface IDialogService
{
    /// <summary>Shows a confirmation. Returns true when confirmed; false for cancel, Esc, or when the dialog is replaced.</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request);

    /// <summary>
    /// Shows a custom dialog. The view is located by the ViewLocator naming convention.
    /// Completes when the view model raises <see cref="IDialogContent.CloseRequested"/> (or the user presses Esc).
    /// </summary>
    Task ShowAsync(object dialogViewModel);
}
