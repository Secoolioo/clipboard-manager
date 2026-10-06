using System.Collections.Concurrent;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;

namespace ClipboardManager.Tests.Store;

[Trait("Category", "Store")]
public sealed class DbWorkerTests : IAsyncLifetime
{
    private readonly TempDataDirectory _dir = new();
    private readonly ConcurrentQueue<HistoryChangeBatch> _batches = new();
    private DbWorker _worker = null!;

    public async ValueTask InitializeAsync()
    {
        _worker = new DbWorker(FileLog.Null);
        _worker.Changed += _batches.Enqueue;
        await _worker.OpenAsync(_dir.Paths, memoryOnly: false, HistoryLimits.For(100));
    }

    public async ValueTask DisposeAsync()
    {
        await _worker.DisposeAsync();
        _dir.Dispose();
    }

    private Task<CaptureResult?> Capture(string text, long now) => _worker.CaptureAsync(text, ContentHash.Compute(text), now);

    [Fact]
    public async Task Open_publishes_a_reset_batch()
    {
        Assert.True(_batches.TryPeek(out var first));
        Assert.NotNull(first.ResetTo);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Batches_are_strictly_versioned_in_submission_order()
    {
        var tasks = Enumerable.Range(0, 50).Select(i => Capture("item " + i, 1000 + i)).ToArray();
        await Task.WhenAll(tasks);

        var versions = _batches.Select(b => b.Version).ToList();
        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    [Fact]
    public async Task Capture_followed_by_delete_never_resurrects_the_entry_in_the_index()
    {
        var index = new HistoryIndex();
        var result = await Capture("ghost?", 1);
        var captureTask = Capture("ghost?", 2); // promotion queued before the delete
        var deleteTask = _worker.DeleteAsync(result!.Entry!.Id);
        await Task.WhenAll(captureTask, deleteTask);

        foreach (var batch in _batches.OrderBy(b => b.Version))
        {
            index.Apply(batch);
        }

        Assert.Null(index.Find(result.Entry.Id));
    }

    [Fact]
    public async Task Stale_batches_are_ignored_by_the_index()
    {
        var index = new HistoryIndex();
        await Capture("a", 1);
        await Capture("b", 2);
        var ordered = _batches.OrderBy(b => b.Version).ToList();

        foreach (var batch in ordered.AsEnumerable().Reverse())
        {
            index.Apply(batch);
        }

        // Applying newest first means older batches are dropped.
        Assert.Equal(ordered[^1].Version, index.Version);
    }

    [Fact]
    public async Task Index_snapshot_orders_pins_first_by_pin_time_and_history_by_recency()
    {
        var index = new HistoryIndex();
        var a = (await Capture("a", 10))!.Entry!;
        var b = (await Capture("b", 20))!.Entry!;
        await Capture("c", 30);
        await _worker.SetPinnedAsync(a.Id, true, 40);
        await _worker.SetPinnedAsync(b.Id, true, 50);

        foreach (var batch in _batches.OrderBy(x => x.Version))
        {
            index.Apply(batch);
        }

        var snapshot = index.Snapshot();
        Assert.Equal(["b", "a"], snapshot.Pinned.Select(e => e.SearchText));
        Assert.Equal(["c"], snapshot.History.Select(e => e.SearchText));
    }
}
