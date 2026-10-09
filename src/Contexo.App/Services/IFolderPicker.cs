namespace Contexo.App.Services;

public interface IFolderPicker
{
    /// <summary>Lets the user choose one folder. Returns null when cancelled.</summary>
    Task<string?> PickFolderAsync(string? initialPath);
}

public interface IFilePicker
{
    /// <summary>Lets the user choose a folder to save an exported file into. Returns null when cancelled.</summary>
    Task<string?> PickSaveFolderAsync();
}
