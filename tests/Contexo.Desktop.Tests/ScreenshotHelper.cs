using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Contexo.Desktop.Tests;

/// <summary>
/// Renders a control with Skia and writes <c>artifacts/screenshots/{name}.png</c> under the repository root
/// (git-ignored), so people and agents can look at a screen. Later UI tasks reuse it.
/// </summary>
public static class ScreenshotHelper
{
    /// <returns>The full path of the PNG that was written.</returns>
    public static string Capture(Control control, string name, double width = 1100, double height = 720)
    {
        var window = control as Window;
        var ownsWindow = false;
        if (window is null)
        {
            window = new Window { Width = width, Height = height, Content = control };
            ownsWindow = true;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered");

        var directory = Path.Combine(FindRepositoryRoot(), "artifacts", "screenshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".png");
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        if (ownsWindow)
        {
            window.Content = null;
            window.Close();
        }

        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Contexo.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Path.GetTempPath();
    }
}
