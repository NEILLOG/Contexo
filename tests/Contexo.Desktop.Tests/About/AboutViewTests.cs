using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Contexo.App.About;
using Contexo.App.Services;
using Contexo.Core.Abstractions;
using Contexo.Desktop.Views.About;

namespace Contexo.Desktop.Tests.About;

public sealed class AboutViewTests
{
    private const string Changelog = "# 版本紀錄\n\n## 1.4.2 - 2026-10-06\n- AI 軟體連線狀態改為三種\n- 修正大型 Excel 讀取逾時\n\n## 1.4.0 - 2026-09-22\n- 新增匯出問題回報\n";

    // The real desktop (what the product compares with); a temporary folder where there is none (Linux CI).
    private static readonly string Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) is { Length: > 0 } desktop
        ? desktop
        : Path.Combine(Path.GetTempPath(), "contexo-view-desktop");

    private static AboutViewModel CreateViewModel(FakeExporter? exporter = null, string? changelog = Changelog, bool clipboardWorks = true) =>
        new(exporter ?? new FakeExporter(), new FakePicker(Desktop), new FakeLauncher(), new FakeClipboard(clipboardWorks), new FakeEmbedding(),
            new FakePaths(), null, new AppVersionInfo("1.4.2", "1.4.2+37.g3f2a9c1", "3f2a9c1", new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero)), changelog);

    private static Window Show(Control view, double width = 1000, double height = 900)
    {
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void About_page_shows_the_version_card_the_export_card_and_the_version_history()
    {
        var view = new AboutView { DataContext = CreateViewModel() };
        var window = Show(view);

        Assert.Equal("1.4.2", Find<TextBlock>(window, "VersionValue").Text);
        Assert.StartsWith("1.4.2+37.g3f2a9c1", Find<TextBlock>(window, "BuildValue").Text);
        Assert.Equal("bge-small-zh-v1.5（int8）", Find<TextBlock>(window, "ModelValue").Text);
        Assert.True(Find<Border>(window, "ExportCard").IsEffectivelyVisible);
        Assert.True(Find<Border>(window, "ChangelogCard").IsEffectivelyVisible);

        // The required item is ticked and cannot be changed; the others follow the defaults of the mockup.
        var system = Find<CheckBox>(window, "SystemInfoCheck");
        Assert.True(system.IsChecked);
        Assert.False(system.IsEnabled);
        Assert.True(Find<CheckBox>(window, "SettingsCheck").IsChecked);
        Assert.True(Find<CheckBox>(window, "LogsCheck").IsChecked);
        Assert.True(Find<CheckBox>(window, "FailedFilesCheck").IsChecked);
        Assert.False(Find<CheckBox>(window, "FullPathsCheck").IsChecked);

        Assert.False(Find<Border>(window, "ResultBox").IsEffectivelyVisible);
        ScreenshotHelper.Capture(window, "about-light");
        window.Close();
    }

    [AvaloniaFact]
    public void Version_history_card_is_hidden_without_a_changelog()
    {
        var window = Show(new AboutView { DataContext = CreateViewModel(changelog: null) });

        Assert.False(Find<Border>(window, "ChangelogCard").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(true, 1.25)]
    public void About_page_renders_in_light_and_dark_and_at_large_text(bool dark, double scale)
    {
        Application.Current!.RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        var view = new LayoutTransformControl
        {
            LayoutTransform = new Avalonia.Media.ScaleTransform(scale, scale),
            Child = new AboutView { DataContext = CreateViewModel() },
        };

        var window = Show(view, 900, 1100);
        var path = ScreenshotHelper.Capture(window, $"about-{(dark ? "dark" : "light")}-{scale:0.00}");

        Assert.True(new FileInfo(path).Length > 1000);
        window.Close();
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }

    [AvaloniaFact]
    public async Task Pressing_export_shows_the_result_and_the_open_folder_button_reveals_the_file()
    {
        var launcher = new FakeLauncher();
        var viewModel = new AboutViewModel(new FakeExporter(), new FakePicker(Desktop), launcher, new FakeClipboard(true), new FakeEmbedding(), new FakePaths(), null,
            new AppVersionInfo("1.0.0", "1.0.0", null, null), Changelog);
        var window = Show(new AboutView { DataContext = viewModel });

        await viewModel.Export!.ExportCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<Border>(window, "ResultBox").IsEffectivelyVisible);
        var resultText = Find<TextBlock>(window, "ResultTextBlock").Text;
        Assert.StartsWith("已儲存到", resultText);
        Assert.EndsWith("Contexo問題回報_20261008_0004.zip（1.2 MB）", resultText);
        Find<Button>(window, "OpenFolderButton").Command!.Execute(null);
        Assert.Equal([Path.Combine(Desktop, "Contexo問題回報_20261008_0004.zip")], launcher.Revealed);
        ScreenshotHelper.Capture(window, "about-export-done");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Unticking_an_item_changes_the_options_and_a_failure_shows_a_plain_message()
    {
        var exporter = new FakeExporter { Failure = new IOException("disk full") };
        var viewModel = CreateViewModel(exporter);
        var window = Show(new AboutView { DataContext = viewModel });

        Find<CheckBox>(window, "LogsCheck").IsChecked = false;
        Find<CheckBox>(window, "FullPathsCheck").IsChecked = true;
        await viewModel.Export!.ExportCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(exporter.Options!.IncludeRecentLogs);
        Assert.True(exporter.Options.IncludeFullPaths);
        Assert.True(Find<Border>(window, "ErrorBox").IsEffectivelyVisible);
        Assert.StartsWith("存檔時發生問題", Find<TextBlock>(window, "ErrorTextBlock").Text);
        Assert.False(Find<Border>(window, "ResultBox").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Copy_version_button_copies_and_the_fallback_text_box_appears_when_the_clipboard_fails()
    {
        var okClipboard = new FakeClipboard(true);
        var viewModel = new AboutViewModel(new FakeExporter(), new FakePicker(Desktop), new FakeLauncher(), okClipboard, new FakeEmbedding(), new FakePaths(), null,
            new AppVersionInfo("1.4.2", "1.4.2", null, null), null);
        var window = Show(new AboutView { DataContext = viewModel });
        Find<Button>(window, "CopyVersionButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("版本：1.4.2", okClipboard.Text);
        Assert.False(Find<TextBox>(window, "VersionTextBox").IsEffectivelyVisible);
        window.Close();

        var broken = CreateViewModel(clipboardWorks: false);
        var window2 = Show(new AboutView { DataContext = broken });
        Find<Button>(window2, "CopyVersionButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<TextBox>(window2, "VersionTextBox").IsEffectivelyVisible);
        Assert.Contains("版本：1.4.2", Find<TextBox>(window2, "VersionTextBox").Text);
        window2.Close();
    }

    [AvaloniaFact]
    public async Task Start_up_error_screen_export_button_runs_the_same_export_flow()
    {
        var exporter = new FakeExporter();
        var about = CreateViewModel(exporter);
        var error = new StartupErrorViewModel();
        var window = Show(new StartupErrorView { DataContext = error });
        Assert.False(Find<Button>(window, "ExportButton").IsEnabled);
        ScreenshotHelper.Capture(window, "startup-error-disabled", 700, 400);

        error.Attach(about.CreateExportFlow()!);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<Button>(window, "ExportButton").IsEnabled);

        await error.Export!.ExportCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(exporter.Options!.IncludeSettings);
        Assert.False(exporter.Options.IncludeFullPaths);
        Assert.True(Find<Border>(window, "ResultBox").IsEffectivelyVisible);
        Assert.StartsWith("已儲存到", Find<TextBlock>(window, "ResultTextBlock").Text);
        ScreenshotHelper.Capture(window, "startup-error-exported", 700, 400);
        window.Close();
    }

    [AvaloniaFact]
    public void Start_up_error_screen_without_a_flow_has_a_disabled_button()
    {
        var window = Show(new StartupErrorView { DataContext = new StartupErrorViewModel() });

        Assert.False(Find<Button>(window, "ExportButton").IsEnabled);
        Assert.False(Find<Border>(window, "ResultBox").IsEffectivelyVisible);
        window.Close();
    }

    // ---- fakes -----------------------------------------------------------------------------------------------

    private sealed class FakeExporter : IDiagnosticsExporter
    {
        public DiagnosticsOptions? Options { get; private set; }

        public Exception? Failure { get; init; }

        public Task<DiagnosticsResult> ExportAsync(string destinationDirectory, DiagnosticsOptions options, CancellationToken cancellationToken)
        {
            Options = options;
            return Failure is not null
                ? Task.FromException<DiagnosticsResult>(Failure)
                : Task.FromResult(new DiagnosticsResult(Path.Combine(destinationDirectory, "Contexo問題回報_20261008_0004.zip"), 1_258_291, ["manifest.json"]));
        }
    }

    private sealed class FakePicker(string folder) : IFilePicker
    {
        public Task<string?> PickSaveFolderAsync() => Task.FromResult<string?>(folder);
    }

    private sealed class FakeLauncher : IShellLauncher
    {
        public List<string> Revealed { get; } = [];

        public void OpenFile(string path)
        {
        }

        public void RevealInFileManager(string path) => Revealed.Add(path);

        public void OpenFolder(string path)
        {
        }
    }

    private sealed class FakeClipboard(bool works) : IClipboardService
    {
        public string? Text { get; private set; }

        public Task<bool> TrySetTextAsync(string text)
        {
            if (works)
            {
                Text = text;
            }

            return Task.FromResult(works);
        }
    }

    private sealed class FakeEmbedding : IEmbeddingService
    {
        public string ModelId => "bge-small-zh-v1.5/int8";

        public int Dimensions => 512;

        public bool IsAvailable => true;

        public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePaths : IAppPaths
    {
        public string DataDirectory => Path.GetTempPath();

        public string DatabasePath => Path.Combine(Path.GetTempPath(), "contexo-view-test-missing.db");

        public string LogsDirectory => DataDirectory;

        public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

        public string ModelsDirectory => DataDirectory;

        public string McpExecutablePath => Path.Combine(DataDirectory, "Contexo.Mcp");
    }
}
