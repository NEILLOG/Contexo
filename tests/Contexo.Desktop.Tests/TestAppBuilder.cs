using Avalonia;
using Avalonia.Headless;
using Contexo.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Contexo.Desktop.Tests;

/// <summary>
/// Headless Avalonia application for view tests. It is the real <see cref="Contexo.Desktop.App"/>, so tests get the same
/// FluentTheme, Colors.axaml, Controls.axaml and ViewLocator as the product. Skia renders for real so screenshots work.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Contexo.Desktop.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
