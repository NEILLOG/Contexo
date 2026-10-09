using Contexo.App.Services;

namespace Contexo.Desktop.Platform.Windows;

/// <summary>Stub created by T15. T19 implements the registry Run key.</summary>
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    public bool IsEnabled => false;

    public void SetEnabled(bool enabled)
    {
    }
}
