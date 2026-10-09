namespace Contexo.App.Services;

/// <summary>Opens things with the operating system. Never modifies, moves or deletes the files it is given.</summary>
public interface IShellLauncher
{
    /// <summary>Opens a file with its default program.</summary>
    void OpenFile(string path);

    /// <summary>Shows a file selected in Explorer / Finder.</summary>
    void RevealInFileManager(string path);

    /// <summary>Opens a folder in Explorer / Finder.</summary>
    void OpenFolder(string path);
}
