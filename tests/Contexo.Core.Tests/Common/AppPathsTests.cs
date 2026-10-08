using Contexo.Core.Common;

namespace Contexo.Core.Tests.Common;

public sealed class AppPathsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private AppPaths Create(Dictionary<string, string?>? env = null, AppPathsOverrides? overrides = null, string? baseDirectory = null) =>
        new(overrides, name => env is not null && env.TryGetValue(name, out var value) ? value : null, baseDirectory ?? _dir.Combine("app"));

    [Fact]
    public void Data_directory_comes_from_the_environment_variable_and_is_created()
    {
        var data = _dir.Combine("data");
        var paths = Create(new() { [AppPaths.DataDirectoryVariable] = data });

        Assert.Equal(data, paths.DataDirectory);
        Assert.True(Directory.Exists(data));
        Assert.Equal(Path.Combine(data, "contexo.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(data, "settings.json"), paths.SettingsPath);
        Assert.Equal(Path.Combine(data, "logs"), paths.LogsDirectory);
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void Default_data_directory_is_a_contexo_folder_under_local_application_data()
    {
        var paths = new AppPaths(new AppPathsOverrides { DatabasePath = _dir.Combine("x.db") }, _ => null, _dir.Path);

        Assert.Equal("Contexo", Path.GetFileName(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.DataDirectory));
    }

    [Fact]
    public void Explicit_override_wins_over_the_environment_variable()
    {
        var paths = Create(
            new() { [AppPaths.DataDirectoryVariable] = _dir.Combine("env") },
            new AppPathsOverrides { DataDirectory = _dir.Combine("explicit") });

        Assert.Equal(_dir.Combine("explicit"), paths.DataDirectory);
    }

    [Fact]
    public void Database_override_replaces_only_the_database_path_and_creates_its_folder()
    {
        var data = _dir.Combine("data");
        var db = _dir.Combine("elsewhere", "my.db");
        var paths = Create(new() { [AppPaths.DataDirectoryVariable] = data }, new AppPathsOverrides { DatabasePath = db });

        Assert.Equal(db, paths.DatabasePath);
        Assert.True(Directory.Exists(_dir.Combine("elsewhere")));
        Assert.Equal(Path.Combine(data, "logs"), paths.LogsDirectory);
    }

    [Fact]
    public void Models_directory_prefers_the_installed_folder_next_to_the_program()
    {
        var app = _dir.Combine("app");
        Directory.CreateDirectory(Path.Combine(app, "models"));
        var paths = Create(new() { [AppPaths.DataDirectoryVariable] = _dir.Combine("data") }, baseDirectory: app);

        Assert.Equal(Path.Combine(app, "models"), paths.ModelsDirectory);
    }

    [Fact]
    public void Models_directory_falls_back_to_the_data_directory()
    {
        var data = _dir.Combine("data");
        var paths = Create(new() { [AppPaths.DataDirectoryVariable] = data });

        Assert.Equal(Path.Combine(data, "models"), paths.ModelsDirectory);
        Assert.True(Directory.Exists(paths.ModelsDirectory));
    }

    [Fact]
    public void Models_directory_can_be_overridden()
    {
        var fromEnv = Create(new()
        {
            [AppPaths.DataDirectoryVariable] = _dir.Combine("data"),
            [AppPaths.ModelsDirectoryVariable] = _dir.Combine("env-models"),
        });
        Assert.Equal(_dir.Combine("env-models"), fromEnv.ModelsDirectory);

        var explicitOverride = Create(
            new() { [AppPaths.DataDirectoryVariable] = _dir.Combine("data") },
            new AppPathsOverrides { ModelsDirectory = _dir.Combine("arg-models") });
        Assert.Equal(_dir.Combine("arg-models"), explicitOverride.ModelsDirectory);
    }

    [Fact]
    public void Mcp_executable_sits_next_to_the_program_with_a_platform_specific_name()
    {
        var app = _dir.Combine("app");
        var paths = Create(baseDirectory: app, env: new() { [AppPaths.DataDirectoryVariable] = _dir.Combine("data") });

        Assert.Equal(Path.Combine(app, OperatingSystem.IsWindows() ? "Contexo.Mcp.exe" : "Contexo.Mcp"), paths.McpExecutablePath);
    }
}
