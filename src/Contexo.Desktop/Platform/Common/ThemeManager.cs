using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using Contexo.Core.Abstractions;

namespace Contexo.Desktop.Platform.Common;

/// <summary>
/// Applies <see cref="AppSettings.Theme"/> to <see cref="Application.RequestedThemeVariant"/> and follows later changes.
/// "System" is <see cref="ThemeVariant.Default"/>, which Avalonia keeps in sync with the Windows / macOS light-dark setting.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private readonly Application _application;
    private readonly ISettingsStore _settings;

    public ThemeManager(Application application, ISettingsStore settings)
    {
        _application = application;
        _settings = settings;
        Apply(settings.Current.Theme);
        _settings.Changed += OnSettingsChanged;
    }

    public static ThemeVariant ToVariant(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public void Apply(ThemePreference preference) => _application.RequestedThemeVariant = ToVariant(preference);

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        Dispatcher.UIThread.Post(() => Apply(settings.Theme));

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
