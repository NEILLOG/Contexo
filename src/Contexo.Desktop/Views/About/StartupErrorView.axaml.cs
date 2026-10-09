using Avalonia.Controls;
using Contexo.App.About;
using Microsoft.Extensions.DependencyInjection;

namespace Contexo.Desktop.Views.About;

public sealed partial class StartupErrorView : UserControl
{
    public StartupErrorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConnectExport();
    }

    /// <summary>
    /// The start-up error screen is created before the normal pages and without dependency injection,
    /// so the view hands it the same export flow the About page uses (taken from the running application's services).
    /// Nothing happens under the headless test lifetime, where there are no services; tests attach the flow themselves.
    /// </summary>
    private void ConnectExport()
    {
        if (DataContext is StartupErrorViewModel { CanExport: false } error
            && (Avalonia.Application.Current as App)?.Services?.GetService<AboutViewModel>()?.CreateExportFlow() is { } flow)
        {
            error.Attach(flow);
        }
    }
}
