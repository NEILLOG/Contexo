using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Shell;
using Contexo.App.ViewModels;

namespace Contexo.App.Folders;

/// <summary>Which part of the folder list a row belongs to. The order is the display order.</summary>
public enum FolderGroup
{
    Problem,
    Running,
    Done,
}

/// <summary>Group title row in the folder list, e.g. "已完成 · 12 個資料夾" with an optional expand / collapse button.</summary>
public sealed record FolderGroupHeader(string Text, bool CanToggle, string ToggleText, ICommand? ToggleCommand);

/// <summary>One line of the folder list. Properties are updated in place so the list does not flicker while progress changes.</summary>
public sealed partial class FolderRowViewModel : ViewModelBase
{
    private readonly FoldersViewModel _owner;

    internal FolderRowViewModel(FoldersViewModel owner, long id)
    {
        _owner = owner;
        Id = id;
    }

    public long Id { get; }

    internal FolderGroup Group { get; set; }

    public bool HasProblem => Group == FolderGroup.Problem;

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial string Path { get; private set; } = "";

    /// <summary>The path shortened in the middle when it is long; the full path is the tooltip.</summary>
    [ObservableProperty]
    public partial string DisplayPath { get; private set; } = "";

    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsWarn))]
    [NotifyPropertyChangedFor(nameof(IsOff))]
    public partial StatusTone Tone { get; private set; } = StatusTone.Ok;

    public bool IsOk => Tone == StatusTone.Ok;

    public bool IsRunning => Tone == StatusTone.Running;

    public bool IsWarn => Tone == StatusTone.Warn;

    public bool IsOff => Tone == StatusTone.Muted;

    internal void Update(string name, string path, string countText, string statusText, StatusTone tone, FolderGroup group)
    {
        Name = name;
        Path = path;
        DisplayPath = PathRelations.ShortenMiddle(path);
        CountText = countText;
        StatusText = statusText;
        Tone = tone;
        Group = group;
        OnPropertyChanged(nameof(HasProblem));
    }

    [RelayCommand]
    private Task PickSubfolders() => _owner.OpenSubfolderPickerAsync(this);

    [RelayCommand]
    private void OpenInFileManager() => _owner.OpenFolderInFileManager(this);

    [RelayCommand]
    private void RevealInFileManager() => _owner.RevealFolderInFileManager(this);

    [RelayCommand]
    private void Rescan() => _owner.RescanFolder(this);

    [RelayCommand]
    private Task Remove() => _owner.RemoveFolderAsync(this);

    [RelayCommand]
    private Task ExcludeFromAi() => _owner.ExcludeFolderAsync(this);
}
