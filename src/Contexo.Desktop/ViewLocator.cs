using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Contexo.Desktop;

/// <summary>
/// Maps a view model to its view by naming convention:
/// <c>Contexo.App.&lt;Area&gt;.&lt;Name&gt;ViewModel</c> becomes <c>Contexo.Desktop.Views.&lt;Area&gt;.&lt;Name&gt;View</c>.
/// Registered once in App.axaml; pages and dialogs added later only need a correctly named view.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private const string ViewModelNamespacePrefix = "Contexo.App.";
    private const string ViewModelSuffix = "ViewModel";
    private const string ViewNamespacePrefix = "Contexo.Desktop.Views.";
    private const string ViewSuffix = "View";

    public bool Match(object? data) =>
        data is not null
        && data.GetType().FullName is { } name
        && name.StartsWith(ViewModelNamespacePrefix, StringComparison.Ordinal)
        && name.EndsWith(ViewModelSuffix, StringComparison.Ordinal);

    public Control? Build(object? data)
    {
        if (data is null)
        {
            return null;
        }

        var viewType = ResolveViewType(data.GetType());
        if (viewType is not null && Activator.CreateInstance(viewType) is Control view)
        {
            return view;
        }

        return new TextBlock { Text = $"找不到畫面：{data.GetType().FullName}" };
    }

    /// <summary>The view type for a view model type, or null when the naming convention finds none.</summary>
    internal static Type? ResolveViewType(Type viewModelType)
    {
        var fullName = viewModelType.FullName;
        if (fullName is null
            || !fullName.StartsWith(ViewModelNamespacePrefix, StringComparison.Ordinal)
            || !fullName.EndsWith(ViewModelSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var middle = fullName[ViewModelNamespacePrefix.Length..^ViewModelSuffix.Length];
        var viewName = ViewNamespacePrefix + middle + ViewSuffix;
        return typeof(ViewLocator).Assembly.GetType(viewName, throwOnError: false);
    }
}
