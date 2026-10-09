using Contexo.App.ViewModels;

namespace Contexo.App.About;

/// <summary>Shown instead of the normal pages when the database could not be initialised. T20 wires up the export button.</summary>
public sealed class StartupErrorViewModel : ViewModelBase
{
    public string Title => "Contexo 無法啟動";

    public string Message =>
        "Contexo 暫時無法讀取自己的資料，所以目前無法使用。您的原始檔案完全沒有受到影響。請關閉後再開啟一次；如果仍然發生，請匯出問題回報並交給負責的人員。";

    /// <summary>The export button stays disabled until T20 connects it.</summary>
    public bool CanExport => false;
}
