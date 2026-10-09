namespace Contexo.Core.Integrations;

public enum ClientPlatform
{
    Windows,
    MacOS,
    /// <summary>Not a supported product platform. Every AI client reports "not installed".</summary>
    Other,
}

/// <summary>
/// Root directories used to locate each AI client's files. Defaults come from the running OS;
/// tests point them at a temporary folder so real user settings are never touched.
/// </summary>
public sealed record ClientPathOptions
{
    public ClientPlatform Platform { get; init; } =
        OperatingSystem.IsWindows() ? ClientPlatform.Windows :
        OperatingSystem.IsMacOS() ? ClientPlatform.MacOS :
        ClientPlatform.Other;

    /// <summary>%APPDATA% (Roaming).</summary>
    public string AppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>%LOCALAPPDATA%.</summary>
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%USERPROFILE% / the home directory.</summary>
    public string UserProfile { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>macOS system-wide applications folder.</summary>
    public string ApplicationsDirectory { get; init; } = "/Applications";
}
