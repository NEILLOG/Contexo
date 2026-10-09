#pragma warning disable CS8524 // Unnamed enum values are deliberately not covered, so a missing named member (CS8509) still fails the build.

using Contexo.Core.Abstractions;

namespace Contexo.App.UserMessages;

/// <summary>
/// Plain Traditional Chinese wording for technical enums. The switch expressions have no discard arm on purpose:
/// adding an enum member without a translation fails the build (CS8509 is an error here).
/// </summary>
public static class ErrorText
{
    public static string ToText(DocumentErrorCode code) => code switch
    {
        DocumentErrorCode.None => "正常",
        DocumentErrorCode.PasswordProtected => "有密碼保護",
        DocumentErrorCode.Locked => "正被其他程式開啟",
        DocumentErrorCode.Corrupted => "檔案可能已損壞",
        DocumentErrorCode.TooLarge => "超過大小上限",
        DocumentErrorCode.Unsupported => "不支援這種檔案",
        DocumentErrorCode.Timeout => "讀取時間過長",
        DocumentErrorCode.AccessDenied => "沒有權限讀取",
        DocumentErrorCode.Unknown => "讀取時發生問題",
    };

    public static string ToText(FolderState state) => state switch
    {
        FolderState.Active => "正常",
        FolderState.Unavailable => "無法存取",
        FolderState.AwaitingDeletionConfirmation => "等待您確認",
    };

    public static string ToText(ClientConnectionState state) => state switch
    {
        ClientConnectionState.NotInstalled => "沒有安裝",
        ClientConnectionState.NotAdded => "尚未加入",
        ClientConnectionState.WaitingForConnection => "等待連線",
        ClientConnectionState.Connected => "已連線",
        ClientConnectionState.NeedsRepair => "需要修復",
    };
}
