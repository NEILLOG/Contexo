using Contexo.Core.Abstractions;

namespace Contexo.Core.Common;

/// <summary>Explicit locations that win over environment variables and defaults (e.g. the --db / --models arguments of Contexo.Mcp).</summary>
public sealed record AppPathsOverrides
{
    public string? DataDirectory { get; init; }
    public string? DatabasePath { get; init; }
    public string? ModelsDirectory { get; init; }
}

/// <summary>Default <see cref="IAppPaths"/>. Directories are created on first access.</summary>
public sealed class AppPaths : IAppPaths
{
    public const string DataDirectoryVariable = "CONTEXO_DATA_DIR";
    public const string ModelsDirectoryVariable = "CONTEXO_MODELS_DIR";

    private readonly AppPathsOverrides _overrides;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly string _baseDirectory;
    private readonly Lazy<string> _dataDirectory;
    private readonly Lazy<string> _logsDirectory;
    private readonly Lazy<string> _modelsDirectory;

    public AppPaths(AppPathsOverrides? overrides = null)
        : this(overrides, Environment.GetEnvironmentVariable, AppContext.BaseDirectory)
    {
    }

    internal AppPaths(AppPathsOverrides? overrides, Func<string, string?> getEnvironmentVariable, string baseDirectory)
    {
        _overrides = overrides ?? new AppPathsOverrides();
        _getEnvironmentVariable = getEnvironmentVariable;
        _baseDirectory = baseDirectory;
        _dataDirectory = new Lazy<string>(ResolveDataDirectory);
        _logsDirectory = new Lazy<string>(() => Ensure(Path.Combine(DataDirectory, "logs")));
        _modelsDirectory = new Lazy<string>(ResolveModelsDirectory);
    }

    public string DataDirectory => _dataDirectory.Value;

    public string DatabasePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_overrides.DatabasePath))
            {
                var full = Path.GetFullPath(_overrides.DatabasePath);
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent))
                {
                    Ensure(parent);
                }

                return full;
            }

            return Path.Combine(DataDirectory, "contexo.db");
        }
    }

    public string LogsDirectory => _logsDirectory.Value;

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string ModelsDirectory => _modelsDirectory.Value;

    public string McpExecutablePath =>
        Path.Combine(_baseDirectory, OperatingSystem.IsWindows() ? "Contexo.Mcp.exe" : "Contexo.Mcp");

    private string ResolveDataDirectory()
    {
        var configured = FirstNonEmpty(_overrides.DataDirectory, _getEnvironmentVariable(DataDirectoryVariable));
        if (configured is not null)
        {
            return Ensure(Path.GetFullPath(configured));
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Ensure(Path.Combine(local, "Contexo"));
    }

    private string ResolveModelsDirectory()
    {
        var configured = FirstNonEmpty(_overrides.ModelsDirectory, _getEnvironmentVariable(ModelsDirectoryVariable));
        if (configured is not null)
        {
            return Ensure(Path.GetFullPath(configured));
        }

        var installed = Path.Combine(_baseDirectory, "models");
        return Directory.Exists(installed) ? installed : Ensure(Path.Combine(DataDirectory, "models"));
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
