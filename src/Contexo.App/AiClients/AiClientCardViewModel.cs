using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;

namespace Contexo.App.AiClients;

/// <summary>Colour family of the status chip. The view maps these to the Chip.Ok / Running / Warn / Off styles.</summary>
public enum ChipTone
{
    Ok,
    Running,
    Warn,
    Off,
}

/// <summary>One installed AI client on the AI client page: name, status chip, description and one main action.</summary>
public sealed partial class AiClientCardViewModel : ViewModelBase
{
    private readonly AiClientsViewModel _owner;

    internal AiClientCardViewModel(AiClientsViewModel owner, AiClientStatus status)
    {
        _owner = owner;
        ClientId = status.ClientId;
        DisplayName = status.DisplayName;
        Initial = status.DisplayName.Length > 0 ? status.DisplayName[..1].ToUpperInvariant() : "?";
    }

    public string ClientId { get; }

    public string DisplayName { get; }

    /// <summary>Letter shown in the square icon.</summary>
    public string Initial { get; }

    public ClientConnectionState State { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial string Description { get; private set; } = "";

    [ObservableProperty]
    public partial string ActionText { get; private set; } = "";

    /// <summary>True when the action is "加入" or "修復" (accent button); false for "移除".</summary>
    [ObservableProperty]
    public partial bool IsPrimaryAction { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsWarn))]
    [NotifyPropertyChangedFor(nameof(IsOff))]
    public partial ChipTone Tone { get; private set; } = ChipTone.Off;

    /// <summary>Failure shown inside the card, in plain words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; private set; } = "";

    /// <summary>Success hint shown inside the card ("已加入。…", "已複製").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = "";

    /// <summary>True while an action runs; the buttons are disabled.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySnippetCommand))]
    public partial bool IsBusy { get; internal set; }

    public bool IsOk => Tone == ChipTone.Ok;

    public bool IsRunning => Tone == ChipTone.Running;

    public bool IsWarn => Tone == ChipTone.Warn;

    public bool IsOff => Tone == ChipTone.Off;

    public bool HasError => ErrorMessage.Length > 0;

    public bool HasNotice => Notice.Length > 0;

    internal void SetError(string message)
    {
        Notice = "";
        ErrorMessage = message;
    }

    internal void SetNotice(string message)
    {
        ErrorMessage = "";
        Notice = message;
    }

    internal void ClearMessages()
    {
        ErrorMessage = "";
        Notice = "";
    }

    internal void Apply(AiClientStatus status, DateTimeOffset now, TimeZoneInfo zone)
    {
        State = status.State;
        switch (status.State)
        {
            case ClientConnectionState.Connected:
                StatusText = "已連線";
                Tone = ChipTone.Ok;
                Description = DescribeConnected(status, now, zone);
                ActionText = "移除";
                IsPrimaryAction = false;
                break;
            case ClientConnectionState.WaitingForConnection:
                StatusText = "已設定，等待連線";
                Tone = ChipTone.Running;
                Description = $"請把 {DisplayName} 完全關閉後重新開啟。";
                ActionText = "移除";
                IsPrimaryAction = false;
                break;
            case ClientConnectionState.NeedsRepair:
                StatusText = "設定有問題";
                Tone = ChipTone.Warn;
                Description = string.IsNullOrWhiteSpace(status.Problem) ? "設定有問題，請按「修復」。" : status.Problem;
                ActionText = "修復";
                IsPrimaryAction = true;
                break;
            default:
                StatusText = "尚未加入";
                Tone = ChipTone.Off;
                Description = "這台電腦已安裝。";
                ActionText = "加入";
                IsPrimaryAction = true;
                break;
        }
    }

    private static string DescribeConnected(AiClientStatus status, DateTimeOffset now, TimeZoneInfo zone)
    {
        var connected = status.LastConnectedAt is { } at ? TimeText.Absolute(at, now, zone) : "不明";
        var query = status.LastQueryAt is { } q ? TimeText.Relative(q, now, zone) : "還沒有";
        return $"最後連線：{connected} · 最後查詢：{query}";
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task PrimaryAsync() => _owner.RunPrimaryAsync(this);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task CopySnippetAsync() => _owner.CopySnippetAsync(this);
}
