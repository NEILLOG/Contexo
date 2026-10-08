using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Tests.Common;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private AppPaths Paths => new(new AppPathsOverrides { DataDirectory = _dir.Path });

    private JsonSettingsStore CreateStore(TestLogger<JsonSettingsStore>? logger = null) =>
        new(Paths, logger ?? new TestLogger<JsonSettingsStore>());

    private static void AssertDefaults(AppSettings settings)
    {
        var defaults = new AppSettings();
        Assert.Equal(defaults.Theme, settings.Theme);
        Assert.Equal(defaults.FontScale, settings.FontScale);
        Assert.Equal(defaults.EnabledCategories, settings.EnabledCategories);
        Assert.Equal(defaults.FullSpeedOnlyWhenIdle, settings.FullSpeedOnlyWhenIdle);
        Assert.Equal(defaults.LaunchAtStartup, settings.LaunchAtStartup);
        Assert.Equal(defaults.MinimizeToTray, settings.MinimizeToTray);
        Assert.Equal(defaults.MaxFileSizeMb, settings.MaxFileSizeMb);
        Assert.Equal(defaults.FirstRunCompleted, settings.FirstRunCompleted);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var store = CreateStore();

        AssertDefaults(store.Current);
        Assert.False(File.Exists(Paths.SettingsPath));
    }

    [Fact]
    public async Task Saved_settings_are_read_back_by_a_new_store()
    {
        var settings = new AppSettings
        {
            Theme = ThemePreference.Dark,
            FontScale = FontScale.ExtraLarge,
            EnabledCategories = [FileCategory.Pdf, FileCategory.Spreadsheets],
            FullSpeedOnlyWhenIdle = false,
            LaunchAtStartup = false,
            MinimizeToTray = false,
            MaxFileSizeMb = null,
            FirstRunCompleted = true,
        };

        await CreateStore().SaveAsync(settings, CancellationToken.None);
        var reloaded = CreateStore().Current;

        Assert.Equal(ThemePreference.Dark, reloaded.Theme);
        Assert.Equal(FontScale.ExtraLarge, reloaded.FontScale);
        Assert.Equal(new[] { FileCategory.Pdf, FileCategory.Spreadsheets }, reloaded.EnabledCategories);
        Assert.False(reloaded.FullSpeedOnlyWhenIdle);
        Assert.False(reloaded.LaunchAtStartup);
        Assert.False(reloaded.MinimizeToTray);
        Assert.Null(reloaded.MaxFileSizeMb);
        Assert.True(reloaded.FirstRunCompleted);
    }

    [Fact]
    public async Task Save_updates_current_and_raises_changed()
    {
        var store = CreateStore();
        AppSettings? raised = null;
        store.Changed += (_, s) => raised = s;
        var settings = new AppSettings { Theme = ThemePreference.Light };

        await store.SaveAsync(settings, CancellationToken.None);

        Assert.Same(settings, store.Current);
        Assert.Same(settings, raised);
    }

    [Fact]
    public async Task Saving_twice_replaces_the_file_and_leaves_no_temporary_files()
    {
        var store = CreateStore();

        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Light }, CancellationToken.None);
        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, CancellationToken.None);

        Assert.Equal(ThemePreference.Dark, CreateStore().Current.Theme);
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(_dir.Path).Select(Path.GetFileName).ToArray());
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"theme\": ")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"theme\":\"NoSuchTheme\"}")]
    [InlineData("{\"maxFileSizeMb\":\"large\"}")]
    public void Broken_file_gives_defaults_and_logs_a_warning(string content)
    {
        File.WriteAllText(Paths.SettingsPath, content);
        var logger = new TestLogger<JsonSettingsStore>();

        var settings = CreateStore(logger).Current;

        AssertDefaults(settings);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("NoSuchTheme"));
    }

    [Fact]
    public async Task Saving_replaces_a_broken_file()
    {
        File.WriteAllText(Paths.SettingsPath, "garbage");
        var store = CreateStore();
        Assert.Equal(ThemePreference.System, store.Current.Theme);

        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, CancellationToken.None);

        Assert.Equal(ThemePreference.Dark, CreateStore().Current.Theme);
    }

    [Fact]
    public void Unknown_properties_are_ignored_and_missing_ones_take_defaults()
    {
        File.WriteAllText(Paths.SettingsPath, "{\"theme\":\"Dark\",\"somethingNew\":{\"a\":1}}");

        var settings = CreateStore().Current;

        Assert.Equal(ThemePreference.Dark, settings.Theme);
        Assert.Equal(new AppSettings().FontScale, settings.FontScale);
        Assert.Equal(new AppSettings().MaxFileSizeMb, settings.MaxFileSizeMb);
    }
}
