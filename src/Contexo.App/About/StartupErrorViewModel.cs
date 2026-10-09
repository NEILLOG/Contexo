using CommunityToolkit.Mvvm.ComponentModel;
using Contexo.App.ViewModels;

namespace Contexo.App.About;

/// <summary>Shown instead of the normal pages when the database could not be initialised. Its export button uses the same flow as the About page.</summary>
public sealed partial class StartupErrorViewModel : ViewModelBase
{
    public StartupErrorViewModel()
    {
    }

    public StartupErrorViewModel(ProblemReportExport? export) => Export = export;

    public string Title => "Contexo 無法啟動";

    public string Message =>
        "Contexo 暫時無法讀取自己的資料，所以目前無法使用。您的原始檔案完全沒有受到影響。請關閉後再開啟一次；如果仍然發生，請匯出問題回報並交給負責的人員。";

    /// <summary>The export flow with default options. Null until <see cref="Attach"/> is called (the button stays disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial ProblemReportExport? Export { get; private set; }

    public bool CanExport => Export is not null;

    /// <summary>Connects the export flow; the view does this because the screen is created without dependency injection.</summary>
    public void Attach(ProblemReportExport export) => Export = export;
}
