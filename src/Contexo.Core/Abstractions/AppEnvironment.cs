namespace Contexo.Core.Abstractions;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum FontScale
{
    Standard,
    Large,
    ExtraLarge,
}

/// <summary>User-facing file categories. The mapping to extensions lives in <c>FileCategories</c>.</summary>
public enum FileCategory
{
    Documents,
    Presentations,
    Spreadsheets,
    Pdf,
    Email,
    Images,
}

/// <summary>Persisted as settings.json in the data directory. Unknown properties are ignored; missing ones take these defaults.</summary>
public sealed record AppSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public FontScale FontScale { get; init; } = FontScale.Standard;
    public IReadOnlyList<FileCategory> EnabledCategories { get; init; } =
        [FileCategory.Documents, FileCategory.Presentations, FileCategory.Spreadsheets, FileCategory.Pdf, FileCategory.Email];
    public bool FullSpeedOnlyWhenIdle { get; init; } = true;
    public bool LaunchAtStartup { get; init; } = true;
    public bool MinimizeToTray { get; init; } = true;
    /// <summary>Null means no limit.</summary>
    public int? MaxFileSizeMb { get; init; } = 50;
    public bool FirstRunCompleted { get; init; }
}

public interface ISettingsStore
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    /// <summary>Writes atomically (temp file + replace) and raises <see cref="Changed"/>.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>Well-known locations. Directories are created on first access.</summary>
public interface IAppPaths
{
    /// <summary>%LOCALAPPDATA%\Contexo on Windows; LocalApplicationData/Contexo elsewhere. Overridable with CONTEXO_DATA_DIR.</summary>
    string DataDirectory { get; }
    /// <summary>{DataDirectory}\contexo.db</summary>
    string DatabasePath { get; }
    /// <summary>{DataDirectory}\logs</summary>
    string LogsDirectory { get; }
    /// <summary>{DataDirectory}\settings.json</summary>
    string SettingsPath { get; }
    /// <summary>{AppContext.BaseDirectory}\models when it exists, otherwise {DataDirectory}\models. Overridable with CONTEXO_MODELS_DIR.</summary>
    string ModelsDirectory { get; }
    /// <summary>Full path of Contexo.Mcp.exe next to the running app (Contexo.Mcp on non-Windows).</summary>
    string McpExecutablePath { get; }
}
