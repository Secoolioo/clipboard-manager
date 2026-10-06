using System.Threading.Channels;
using ClipboardManager.Core.Diagnostics;

namespace ClipboardManager.Core.History;

public sealed record StoreStatus(StoreMode Mode, StoreNotice Notice);

/// <summary>
/// The only code that touches SQLite. Operations run one at a time in submission order on a
/// thread-pool continuation (no dedicated thread while idle). Every state change is published as a
/// versioned <see cref="HistoryChangeBatch"/> through <see cref="Changed"/> before the operation's
/// task completes, so subscribers always see changes in commit order.
/// </summary>
public sealed class DbWorker : IAsyncDisposable
{
    private const string Category = "DbWorker";

    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly FileLog _log;
    private readonly Task _loop;
    private HistoryStore? _store;
    private long _version;

    public DbWorker(FileLog log)
    {
        _log = log;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Raised on the worker, strictly ordered. Handlers must be fast (post to the UI and return).</summary>
    public event Action<HistoryChangeBatch>? Changed;

    public Task<StoreStatus> OpenAsync(AppPaths? paths, bool memoryOnly, HistoryLimits limits) =>
        Enqueue(
            _ =>
            {
                _store?.Dispose();
                _store = HistoryStore.Open(paths, memoryOnly, limits, _log);
                return (new StoreStatus(_store.Mode, _store.Notice), Reset(_store.LoadAll()));
            },
            new StoreStatus(StoreMode.MemoryFallback, StoreNotice.DatabaseUnavailable),
            requiresStore: false);

    public Task<CaptureResult?> CaptureAsync(string text, byte[] hash, long nowMs) =>
        Enqueue<CaptureResult?>(
            store =>
            {
                var result = store.Capture(text, hash, nowMs);
                return result.Outcome == CaptureOutcome.Unchanged
                    ? (result, null)
                    : (result, Batch(result.Entry, result.Removed));
            },
            null);

    public Task<HistoryEntry?> PromoteAsync(long id, long nowMs) =>
        Enqueue(store =>
        {
            var entry = store.Promote(id, nowMs);
            return (entry, entry is null ? null : Batch(entry, HistoryChangeBatch.NoIds));
        }, (HistoryEntry?)null);

    public Task<HistoryEntry?> SetPinnedAsync(long id, bool pinned, long nowMs) =>
        Enqueue(store =>
        {
            var (entry, removed) = store.SetPinned(id, pinned, nowMs);
            return (entry, entry is null ? null : Batch(entry, removed));
        }, (HistoryEntry?)null);

    public Task<DeletedEntry?> DeleteAsync(long id) =>
        Enqueue(store =>
        {
            var deleted = store.Delete(id);
            return (deleted, deleted is null ? null : Batch(null, [id]));
        }, (DeletedEntry?)null);

    public Task<HistoryEntry?> RestoreAsync(DeletedEntry deleted) =>
        Enqueue(store =>
        {
            var (entry, removed) = store.Restore(deleted);
            return (entry, entry is null ? null : Batch(entry, removed));
        }, (HistoryEntry?)null);

    public Task<bool> ClearAsync(bool includePinned) =>
        Enqueue(store => (true, Reset(store.Clear(includePinned))), false);

    public Task<StoreMode?> SetMemoryOnlyAsync(bool memoryOnly) =>
        Enqueue(store =>
        {
            var entries = store.SetMemoryOnly(memoryOnly);
            return ((StoreMode?)store.Mode, Reset(entries));
        }, (StoreMode?)null);

    public Task<bool> SetLimitsAsync(HistoryLimits limits) =>
        Enqueue(store =>
        {
            var removed = store.SetLimits(limits);
            return (true, removed.Count == 0 ? null : Batch(null, removed));
        }, false);

    public Task<int> CountExceedingAsync(HistoryLimits limits) =>
        Enqueue(store => (store.CountExceeding(limits), (HistoryChangeBatch?)null), 0);

    public Task<string?> GetTextAsync(long id) =>
        Enqueue(store => (store.GetText(id), (HistoryChangeBatch?)null), (string?)null);

    /// <summary>Synchronous bounded shutdown for WM_ENDSESSION, where async continuations may never run.</summary>
    public void Shutdown(TimeSpan timeout)
    {
        _queue.Writer.TryComplete();
        if (!_loop.Wait(timeout))
        {
            _log.Warning(Category, "Shutdown timed out");
            return;
        }

        CloseStore();
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
        CloseStore();
    }

    private void CloseStore()
    {
        try
        {
            _store?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error(Category, "Closing the store failed", ex);
        }

        _store = null;
    }

    private async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            work();
        }
    }

    private Task<T> Enqueue<T>(Func<HistoryStore, (T Result, HistoryChangeBatch? Batch)> operation, T fallback, bool requiresStore = true)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _queue.Writer.TryWrite(() =>
        {
            if (requiresStore && _store is null)
            {
                completion.SetResult(fallback);
                return;
            }

            try
            {
                var (result, batch) = operation(_store!);
                if (batch is not null)
                {
                    Publish(batch);
                }

                completion.SetResult(result);
            }
            catch (Exception ex)
            {
                _log.Error(Category, "Database operation failed", ex);
                completion.SetResult(fallback);
            }
        });

        if (!queued)
        {
            completion.SetResult(fallback);
        }

        return completion.Task;
    }

    private void Publish(HistoryChangeBatch batch)
    {
        var versioned = batch with { Version = ++_version };
        try
        {
            Changed?.Invoke(versioned);
        }
        catch (Exception ex)
        {
            _log.Error(Category, "Change subscriber failed", ex);
        }
    }

    private static HistoryChangeBatch Batch(HistoryEntry? upserted, IReadOnlyList<long> removed) =>
        new(0, upserted is null ? HistoryChangeBatch.NoEntries : [upserted], removed);

    private static HistoryChangeBatch Reset(IReadOnlyList<HistoryEntry> entries) =>
        new(0, HistoryChangeBatch.NoEntries, HistoryChangeBatch.NoIds, entries);
}
