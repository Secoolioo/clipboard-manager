namespace ClipboardManager.Core.History;

public enum StoreMode
{
    /// <summary>Everything persisted in history.db.</summary>
    Persistent,

    /// <summary>User setting: only pinned entries are written to disk.</summary>
    MemoryOnlyByChoice,

    /// <summary>The database could not be used; nothing is persisted.</summary>
    MemoryFallback,
}

public enum StoreNotice
{
    None,
    RecoveredFromCorruption,
    DatabaseInUse,
    DatabaseFromNewerVersion,
    DatabaseUnavailable,
}

/// <summary>
/// Ordered change set produced by the database worker. The UI applies batches strictly in
/// <see cref="Version"/> order, so captures and user actions can never reorder each other.
/// </summary>
public sealed record HistoryChangeBatch(
    long Version,
    IReadOnlyList<HistoryEntry> Upserted,
    IReadOnlyList<long> Removed,
    IReadOnlyList<HistoryEntry>? ResetTo = null)
{
    public static readonly IReadOnlyList<HistoryEntry> NoEntries = [];
    public static readonly IReadOnlyList<long> NoIds = [];
}

public enum CaptureOutcome
{
    Inserted,
    Promoted,
    Unchanged,
}

public sealed record CaptureResult(CaptureOutcome Outcome, HistoryEntry? Entry, IReadOnlyList<long> Removed);

public readonly record struct HistoryLimits(int MaxItems, long MaxTotalChars)
{
    public const int MinItems = 10;
    public const int MaxItemsUpperBound = 5000;
    public const long DefaultMaxTotalChars = 20_000_000;

    public static HistoryLimits For(int maxItems) =>
        new(Math.Clamp(maxItems, MinItems, MaxItemsUpperBound), DefaultMaxTotalChars);
}
