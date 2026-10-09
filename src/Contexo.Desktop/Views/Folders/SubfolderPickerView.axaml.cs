using Avalonia.Controls;
using Avalonia.Input;
using Contexo.App.Folders;

namespace Contexo.Desktop.Views.Folders;

public sealed partial class SubfolderPickerView : UserControl
{
    public SubfolderPickerView() => InitializeComponent();

    /// <summary>Enter saves (Esc is handled by the main window and cancels).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Enter && e.Source is not Button && DataContext is SubfolderPickerViewModel vm)
        {
            if (vm.SaveCommand.CanExecute(null))
            {
                vm.SaveCommand.Execute(null);
            }

            e.Handled = true;
        }
    }
}
