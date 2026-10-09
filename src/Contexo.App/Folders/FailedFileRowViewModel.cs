using CommunityToolkit.Mvvm.Input;
using Contexo.App.UserMessages;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;

namespace Contexo.App.Folders;

/// <summary>What the single button of a failed file does.</summary>
public enum FailedFileAction
{
    /// <summary>Try reading the file again (the problem may be temporary).</summary>
    Retry,

    /// <summary>Stop trying: the file is added to the exclusion list.</summary>
    Skip,
}

/// <summary>One file Contexo could not read, with the plain-language reason and the one reasonable action.</summary>
public sealed partial class FailedFileRowViewModel : ViewModelBase
{
    private readonly FoldersViewModel _owner;

    internal FailedFileRowViewModel(FoldersViewModel owner, DocumentRecord document, string location)
    {
        _owner = owner;
        DocumentId = document.Id;
        Path = document.Path;
        FileName = PathRelations.GetName(document.Path);
        Location = location;
        ErrorCode = document.ErrorCode;
        ReasonText = ErrorText.ToText(document.ErrorCode);
        Action = ActionFor(document.ErrorCode);
        ActionText = Action == FailedFileAction.Retry ? "重試" : "略過";
    }

    public long DocumentId { get; }

    public string Path { get; }

    public string FileName { get; }

    /// <summary>"文件 › 專案資料 › 2025 台中案"</summary>
    public string Location { get; }

    public DocumentErrorCode ErrorCode { get; }

    public string ReasonText { get; }

    public FailedFileAction Action { get; }

    public string ActionText { get; }

    public bool IsSkip => Action == FailedFileAction.Skip;

    /// <summary>Temporary problems can be retried; permanent ones (password, damaged, too large, unsupported) are skipped.</summary>
    public static FailedFileAction ActionFor(DocumentErrorCode code) => code switch
    {
        DocumentErrorCode.Locked or DocumentErrorCode.Timeout or DocumentErrorCode.Unknown or DocumentErrorCode.AccessDenied or DocumentErrorCode.None => FailedFileAction.Retry,
        DocumentErrorCode.PasswordProtected or DocumentErrorCode.Corrupted or DocumentErrorCode.TooLarge or DocumentErrorCode.Unsupported => FailedFileAction.Skip,
        _ => FailedFileAction.Retry,
    };

    [RelayCommand]
    private Task RunAction() => _owner.RunFailedActionAsync(this);

    [RelayCommand]
    private void Open() => _owner.OpenFile(this);

    [RelayCommand]
    private void Reveal() => _owner.RevealFile(this);

    [RelayCommand]
    private Task ExcludeFromAi() => _owner.ExcludeFileAsync(this);
}
