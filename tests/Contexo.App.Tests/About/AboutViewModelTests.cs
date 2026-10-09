using Contexo.App.About;
using Contexo.App.Services;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.About;

public sealed class AboutViewModelTests : IDisposable
{
    private static readonly AppVersionInfo Version = new("1.4.2", "1.4.2+37.g3f2a9c1", "3f2a9c1", new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    private readonly string _folder = Directory.CreateTempSubdirectory("contexo-about-").FullName;
    private readonly FakeDiagnosticsExporter _exporter = new();
    private readonly FakeShellLauncher _launcher = new();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AboutViewModel Create(IClipboardService? clipboard = null, IEmbeddingService? embedding = null, string? changelog = "## 1.4.2 - 2026-10-06\n- 修正") =>
        new(_exporter, new FakeFilePicker(_folder), _launcher, clipboard ?? new FakeClipboard(true), embedding ?? new FakeEmbeddingInfo(),
            new FakePaths(Path.Combine(_folder, "contexo.db")), null, Version, changelog);

    [Fact]
    public void Version_card_shows_the_version_build_and_model()
    {
        var about = Create();

        Assert.Equal("文脈 Contexo", about.ProductName);
        Assert.Equal("1.4.2", about.VersionText);
        Assert.StartsWith("1.4.2+37.g3f2a9c1 · 2026-10-", about.BuildText);
        Assert.Equal("bge-small-zh-v1.5（int8）", about.ModelText);
    }

    [Fact]
    public void A_model_that_is_not_installed_says_so()
    {
        var about = Create(embedding: new FakeEmbeddingInfo(available: false));

        Assert.Equal("bge-small-zh-v1.5（int8），尚未安裝", about.ModelText);
    }

    [Fact]
    public async Task Database_version_is_read_from_the_database_file()
    {
        var about = Create();
        await about.RefreshAsync();
        Assert.Equal("未知", about.DatabaseVersionText); // no database file yet

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_folder, "contexo.db")};Pooling=False"))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 3";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await about.RefreshAsync();

        Assert.Equal("schema 3", about.DatabaseVersionText);
    }

    [Fact]
    public async Task Copy_version_puts_the_text_on_the_clipboard()
    {
        var clipboard = new FakeClipboard(works: true);
        var about = Create(clipboard);

        await about.CopyVersionCommand.ExecuteAsync(null);

        Assert.NotNull(clipboard.Text);
        Assert.Contains("版本：1.4.2", clipboard.Text);
        Assert.Contains("組建：1.4.2+37.g3f2a9c1", clipboard.Text);
        Assert.False(about.ShowVersionText);
        Assert.Equal("已複製", about.CopyStatus);
    }

    [Fact]
    public async Task When_the_clipboard_fails_the_text_is_shown_for_selecting()
    {
        var about = Create(new FakeClipboard(works: false));

        await about.CopyVersionCommand.ExecuteAsync(null);

        Assert.True(about.ShowVersionText);
        Assert.Contains("版本：1.4.2", about.VersionInfoText);
        Assert.Contains("選取", about.CopyStatus);
    }

    [Fact]
    public void Version_history_shows_only_five_versions_and_is_hidden_without_a_file()
    {
        var many = string.Join("\n", Enumerable.Range(1, 7).Select(i => $"## 1.{i}.0 - 2026-01-01\n- x"));

        Assert.Equal(5, Create(changelog: many).Changelog.Count);
        Assert.True(Create(changelog: many).HasChangelog);
        Assert.False(Create(changelog: null).HasChangelog);
        Assert.False(Create(changelog: "").HasChangelog);
    }

    [Fact]
    public void Without_services_the_page_still_works_but_cannot_export()
    {
        var about = new AboutViewModel();

        Assert.False(about.CanExport);
        Assert.Null(about.Export);
        Assert.Null(about.CreateExportFlow());
        Assert.False(string.IsNullOrEmpty(about.VersionText));
    }

    [Fact]
    public void The_start_up_error_screen_gets_its_own_flow_with_default_options()
    {
        var about = Create();
        about.Export!.IncludeFullPaths = true;
        var error = new StartupErrorViewModel();
        Assert.False(error.CanExport);

        var flow = about.CreateExportFlow()!;
        error.Attach(flow);

        Assert.True(error.CanExport);
        Assert.NotSame(about.Export, error.Export);
        Assert.False(error.Export!.IncludeFullPaths);
        Assert.True(error.Export.IncludeSettings);
    }
}
