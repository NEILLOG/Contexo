using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Contexo.Desktop.Views.Folders;

/// <summary>
/// Lets the whole window accept folders dropped from the file manager while a view is on screen.
/// Only folders are used; dropped files are ignored. The window's previous drop setting is restored on <see cref="Dispose"/>.
/// </summary>
internal sealed class FolderDropHandler : IDisposable
{
    private readonly Control _owner;
    private readonly Func<IReadOnlyList<string>, Task> _onDrop;
    private readonly Action<bool> _onHover;
    private TopLevel? _topLevel;
    private bool _previousAllowDrop;

    public FolderDropHandler(Control owner, Func<IReadOnlyList<string>, Task> onDrop, Action<bool> onHover)
    {
        _owner = owner;
        _onDrop = onDrop;
        _onHover = onHover;
    }

    public void Attach()
    {
        _topLevel = TopLevel.GetTopLevel(_owner);
        if (_topLevel is null)
        {
            return;
        }

        _previousAllowDrop = DragDrop.GetAllowDrop(_topLevel);
        DragDrop.SetAllowDrop(_topLevel, true);
        _topLevel.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _topLevel.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        _topLevel.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void Dispose()
    {
        if (_topLevel is null)
        {
            return;
        }

        _topLevel.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        _topLevel.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        _topLevel.RemoveHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(_topLevel, _previousAllowDrop);
        _topLevel = null;
        _onHover(false);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFolders = ExtractFolders(e).Count > 0;
        e.DragEffects = hasFolders ? DragDropEffects.Copy : DragDropEffects.None;
        _onHover(hasFolders);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => _onHover(false);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        _onHover(false);
        var folders = ExtractFolders(e);
        e.Handled = true;
        if (folders.Count > 0)
        {
            await _onDrop(folders);
        }
    }

    private static List<string> ExtractFolders(DragEventArgs e)
    {
        var result = new List<string>();
        var items = e.DataTransfer.TryGetFiles();
        if (items is null)
        {
            return result;
        }

        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                result.Add(path);
            }
        }

        return result;
    }
}
