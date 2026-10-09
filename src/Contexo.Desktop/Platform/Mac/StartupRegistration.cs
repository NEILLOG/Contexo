using Contexo.App.Services;

namespace Contexo.Desktop.Platform.Mac;

/// <summary>Stub created by T15. T19 implements the LaunchAgent.</summary>
public sealed class MacStartupRegistration : IStartupRegistration
{
    public bool IsEnabled => false;

    public void SetEnabled(bool enabled)
    {
    }
}
