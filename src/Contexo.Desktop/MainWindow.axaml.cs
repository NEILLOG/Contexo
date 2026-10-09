using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Contexo.App.Shell;

namespace Contexo.Desktop;

public sealed partial class MainWindow : Window
{
    private ShellViewModel? _shell;

    public MainWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }

        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
            ApplyScale(_shell.FontScaleFactor);
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.FontScaleFactor) && _shell is not null)
        {
            ApplyScale(_shell.FontScaleFactor);
        }
    }

    private void ApplyScale(double factor) =>
        ScaleHost.LayoutTransform = new ScaleTransform(factor, factor);

    /// <summary>Esc cancels and Enter confirms the dialog that is open (Enter never confirms a dangerous action).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || _shell is not { DialogHost.IsOpen: true } shell)
        {
            return;
        }

        if (e.Key == Key.Escape && shell.DialogHost.HandleEscape())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && shell.DialogHost.HandleEnter())
        {
            e.Handled = true;
        }
    }
}
