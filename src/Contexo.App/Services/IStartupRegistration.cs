namespace Contexo.App.Services;

/// <summary>Start Contexo when the user signs in. Implemented per platform (T19).</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
