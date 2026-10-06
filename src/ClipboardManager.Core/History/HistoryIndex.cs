namespace ClipboardManager.Core.History;

/// <summary>Pinned entries (newest pin first) and history (most recently used first).</summary>
public sealed record HistorySnapshot(IReadOnlyList<HistoryEntry> Pinned, IReadOnlyList<HistoryEntry> History)
{
    public static readonly HistorySnapshot Empty = new([], []);

    public int Count => Pinned.Count + History.Count;
}

/// <summary>
/// The UI-thread model of the history. It changes only through versioned batches from the
/// <see cref="DbWorker"/>; stale or duplicate batches are ignored.
/// </summary>
public sealed class HistoryIndex
{
    private readonly Dictionary<long, HistoryEntry> _entries = [];
    private HistorySnapshot? _snapshot;

    public event EventHandler? Changed;

    public long Version { get; private set; }

    public bool IsLoaded { get; private set; }

    public int Count => _entries.Count;

    public HistoryEntry? Find(long id) => _entries.GetValueOrDefault(id);

    public void Apply(HistoryChangeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Version <= Version)
        {
            return;
        }

        Version = batch.Version;
        if (batch.ResetTo is { } all)
        {
            _entries.Clear();
            foreach (var entry in all)
            {
                _entries[entry.Id] = entry;
            }

            IsLoaded = true;
        }

        foreach (var id in batch.Removed)
        {
            _entries.Remove(id);
        }

        foreach (var entry in batch.Upserted)
        {
            _entries[entry.Id] = entry;
        }

        _snapshot = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sorted, immutable view (cached until the next change).</summary>
    public HistorySnapshot Snapshot()
    {
        if (_snapshot is not null)
        {
            return _snapshot;
        }

        var pinned = new List<HistoryEntry>();
        var history = new List<HistoryEntry>(_entries.Count);
        foreach (var entry in _entries.Values)
        {
            (entry.IsPinned ? pinned : history).Add(entry);
        }

        pinned.Sort(static (a, b) => b.PinnedAtMs!.Value.CompareTo(a.PinnedAtMs!.Value) is var c and not 0 ? c : b.Id.CompareTo(a.Id));
        history.Sort(CompareByRecency);
        _snapshot = new HistorySnapshot(pinned, history);
        return _snapshot;
    }

    internal static int CompareByRecency(HistoryEntry a, HistoryEntry b) =>
        b.LastUsedAtMs.CompareTo(a.LastUsedAtMs) is var c and not 0 ? c : b.Id.CompareTo(a.Id);
}
