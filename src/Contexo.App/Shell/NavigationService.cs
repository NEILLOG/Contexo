using Contexo.App.Services;
using Contexo.App.ViewModels;

namespace Contexo.App.Shell;

/// <summary>
/// Singleton that page view models depend on. The <see cref="ShellViewModel"/> listens to <see cref="NavigationRequested"/>;
/// this indirection avoids a dependency cycle (shell → pages → navigation → shell).
/// </summary>
public sealed class NavigationService : INavigationService
{
    public event EventHandler<Type>? NavigationRequested;

    public void NavigateTo<TViewModel>() where TViewModel : ViewModelBase =>
        NavigationRequested?.Invoke(this, typeof(TViewModel));
}
