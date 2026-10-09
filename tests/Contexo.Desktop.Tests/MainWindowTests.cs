using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Contexo.App.About;
using Contexo.App.Shell;
using Contexo.Core.Abstractions;
using Contexo.Desktop.Views.Folders;

namespace Contexo.Desktop.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void Main_window_opens_with_the_product_name_as_title()
    {
        var window = new TestShell().CreateWindow();

        window.Show();

        Assert.True(window.IsVisible);
        Assert.Equal("文脈 Contexo", window.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void Main_window_shows_navigation_page_and_status_bar()
    {
        var test = new TestShell();
        test.Indexing.Raise(new IndexingSnapshot(IndexingState.Indexing, 200, 76, null, null, [], []));
        var window = test.CreateWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Size(900, 600), new Size(window.MinWidth, window.MinHeight));
        var nav = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "NavList");
        Assert.Equal(5, nav.ItemCount);
        Assert.IsType<FoldersView>(window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "PageHost").Presenter?.Child);
        Assert.Equal("處理中 38%", window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ProgressText").Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_navigation_switches_the_page()
    {
        var test = new TestShell();
        var window = test.CreateWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var nav = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "NavList");
        nav.SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<AboutViewModel>(test.Shell.CurrentPage);
        Assert.IsType<Views.About.AboutView>(window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "PageHost").Presenter?.Child);
        window.Close();
    }

    [AvaloniaFact]
    public void First_run_hides_the_navigation_and_shows_the_wizard()
    {
        var test = new TestShell(new AppSettings { FirstRunCompleted = false });
        var window = test.CreateWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var nav = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "NavList");
        Assert.False(nav.GetVisualParent()!.IsEffectivelyVisible);
        Assert.IsType<Views.Folders.FirstRunView>(window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "PageHost").Presenter?.Child);
        ScreenshotHelper.Capture(window, "shell-first-run");
        window.Close();
    }

    [AvaloniaFact]
    public void Startup_error_screen_replaces_the_pages()
    {
        var test = new TestShell();
        var window = test.CreateWindow();
        window.Show();

        test.Shell.ShowStartupError(new StartupErrorViewModel());
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<Views.About.StartupErrorView>(window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "PageHost").Presenter?.Child);
        ScreenshotHelper.Capture(window, "shell-startup-error");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Light, FontScale.Standard)]
    [InlineData(ThemePreference.Light, FontScale.Large)]
    [InlineData(ThemePreference.Light, FontScale.ExtraLarge)]
    [InlineData(ThemePreference.Dark, FontScale.Standard)]
    [InlineData(ThemePreference.Dark, FontScale.Large)]
    [InlineData(ThemePreference.Dark, FontScale.ExtraLarge)]
    public async Task Window_renders_in_every_theme_and_text_size(ThemePreference theme, FontScale scale)
    {
        var test = new TestShell();
        var window = test.CreateWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Change the settings while the window is open: theme and text size must apply immediately.
        await test.Settings.SaveAsync(new AppSettings { Theme = theme, FontScale = scale, FirstRunCompleted = true }, CancellationToken.None);
        TestShell.ApplyTheme(theme);
        Dispatcher.UIThread.RunJobs();

        var expectedVariant = theme == ThemePreference.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Assert.Equal(expectedVariant, window.ActualThemeVariant);

        Assert.True(window.TryFindResource("Brush.Window", window.ActualThemeVariant, out var brush));
        var expectedColor = theme == ThemePreference.Dark ? Color.Parse("#1a2129") : Color.Parse("#fbfcfd");
        Assert.Equal(expectedColor, ((ISolidColorBrush)brush!).Color);

        var factor = scale switch { FontScale.Standard => 1.0, FontScale.Large => 1.12, _ => 1.25 };
        var host = window.GetVisualDescendants().OfType<LayoutTransformControl>().Single(c => c.Name == "ScaleHost");
        var transform = Assert.IsType<ScaleTransform>(host.LayoutTransform);
        Assert.Equal(factor, transform.ScaleX, 3);
        Assert.Equal(factor, transform.ScaleY, 3);

        var path = ScreenshotHelper.Capture(window, $"shell-{theme.ToString().ToLowerInvariant()}-{scale.ToString().ToLowerInvariant()}");
        Assert.True(new FileInfo(path).Length > 1000);
        window.Close();
    }

    [AvaloniaFact]
    public async Task System_theme_follows_the_platform_variant()
    {
        var test = new TestShell();
        var window = test.CreateWindow();
        window.Show();

        await test.Settings.SaveAsync(new AppSettings { Theme = ThemePreference.System, FirstRunCompleted = true }, CancellationToken.None);
        TestShell.ApplyTheme(ThemePreference.System);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
        window.Close();
    }
}
