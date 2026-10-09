using Avalonia;
using Avalonia.Controls;
using Contexo.App.Folders;

namespace Contexo.Desktop.Views.Folders;

public sealed partial class FirstRunView : UserControl
{
    private FolderDropHandler? _drop;

    public FirstRunView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _drop?.Dispose();
        _drop = new FolderDropHandler(
            this,
            paths =>
            {
                // Only step 1 is about folders; dropping elsewhere does nothing.
                if (DataContext is FirstRunViewModel { IsStep1: true } vm)
                {
                    vm.AddDroppedFolders(paths);
                }

                return Task.CompletedTask;
            },
            hovering => DropOverlay.IsVisible = hovering && DataContext is FirstRunViewModel { IsStep1: true });
        _drop.Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _drop?.Dispose();
        _drop = null;
        base.OnDetachedFromVisualTree(e);
    }
}
