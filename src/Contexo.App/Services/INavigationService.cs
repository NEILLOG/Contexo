using Contexo.App.ViewModels;

namespace Contexo.App.Services;

public interface INavigationService
{
    /// <summary>Switches the main window to the page whose view model is <typeparamref name="TViewModel"/>.</summary>
    void NavigateTo<TViewModel>() where TViewModel : ViewModelBase;
}
