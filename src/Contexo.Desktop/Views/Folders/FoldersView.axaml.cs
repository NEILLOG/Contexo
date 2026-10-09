using Avalonia.Controls;
using Contexo.App.Folders;

namespace Contexo.Desktop.Views.Folders;

public sealed partial class FoldersView : UserControl
{
    private FolderDropHandler? _drop;

    public FoldersView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _drop?.Dispose();
        _drop = new FolderDropHandler(
            this,
            paths => DataContext is FoldersViewModel vm ? vm.AddDroppedFoldersAsync(paths) : Task.CompletedTask,
            hovering => DropOverlay.IsVisible = hovering);
        _drop.Attach();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _drop?.Dispose();
        _drop = null;
        base.OnDetachedFromVisualTree(e);
    }
}
