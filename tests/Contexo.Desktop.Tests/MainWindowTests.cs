using Avalonia.Headless.XUnit;

namespace Contexo.Desktop.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void Main_window_opens_with_the_product_name_as_title()
    {
        var window = new MainWindow();

        window.Show();

        Assert.True(window.IsVisible);
        Assert.Equal("文脈 Contexo", window.Title);
        window.Close();
    }
}
