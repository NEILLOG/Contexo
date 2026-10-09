using Contexo.Core.Indexing;

namespace Contexo.Core.Tests.Indexing;

public sealed class WorkQueueTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static WorkItem Item(string name, int days, long folder = 1, bool isNew = false) =>
        new(folder, "/data/" + name, 10, Base.AddDays(days), false, isNew);

    private static List<string> Drain(WorkQueue queue)
    {
        var names = new List<string>();
        while (queue.TryDequeue(out var item))
        {
            names.Add(Path.GetFileName(item.Path));
        }

        return names;
    }

    [Fact]
    public void Newest_files_come_first()
    {
        var queue = new WorkQueue();
        queue.Enqueue(Item("old", 1), false);
        queue.Enqueue(Item("new", 9), false);
        queue.Enqueue(Item("mid", 5), false);

        Assert.Equal(["new", "mid", "old"], Drain(queue));
    }

    [Fact]
    public void Priority_files_jump_the_queue_and_the_latest_priority_file_is_first()
    {
        var queue = new WorkQueue();
        queue.Enqueue(Item("new", 9), false);
        queue.Enqueue(Item("retry1", 1), true);
        queue.Enqueue(Item("retry2", 2), true);

        Assert.Equal(["retry2", "retry1", "new"], Drain(queue));
    }

    [Fact]
    public void A_path_is_queued_once_and_can_be_promoted()
    {
        var queue = new WorkQueue();
        Assert.True(queue.Enqueue(Item("a", 1), false));
        Assert.True(queue.Enqueue(Item("b", 2), false));
        Assert.False(queue.Enqueue(Item("A", 1), true));

        Assert.Equal(2, queue.Count);
        Assert.Equal(["A", "b"], Drain(queue));
    }

    [Fact]
    public void Folder_counts_and_removal_only_touch_that_folder()
    {
        var queue = new WorkQueue();
        queue.Enqueue(Item("a", 1, folder: 1, isNew: true), false);
        queue.Enqueue(Item("b", 2, folder: 1), false);
        queue.Enqueue(Item("c", 3, folder: 2), true);

        Assert.Equal((2, 1), queue.CountForFolder(1));
        Assert.Equal((1, 0), queue.CountForFolder(2));
        Assert.Equal(2, queue.RemoveFolder(1));
        Assert.Equal(["c"], Drain(queue));
    }
}
