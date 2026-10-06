namespace ClipboardManager.Core.Capture;

/// <summary>
/// Remembers which history entry (or which skip reason) corresponds to which clipboard sequence
/// number, so the popup can mark the entry that is really in the clipboard right now, or explain
/// why the current content is not in the history.
/// </summary>
public sealed class CurrentClipTracker
{
    private uint _entrySequence;
    private long? _entryId;
    private uint _skipSequence;
    private SkipReason _skipReason;

    public void EntryIsCurrent(uint sequence, long entryId)
    {
        _entrySequence = sequence;
        _entryId = entryId;
    }

    public void ContentSkipped(uint sequence, SkipReason reason)
    {
        _skipSequence = sequence;
        _skipReason = reason;
    }

    public void EntryRemoved(long entryId)
    {
        if (_entryId == entryId)
        {
            _entryId = null;
        }
    }

    /// <summary>The entry whose content is in the clipboard now, or null when unknown.</summary>
    public long? CurrentEntry(uint clipboardSequence) =>
        _entryId is not null && clipboardSequence == _entrySequence ? _entryId : null;

    /// <summary>Why the current clipboard content was not stored (silent reasons are filtered out).</summary>
    public SkipReason CurrentSkip(uint clipboardSequence) =>
        clipboardSequence == _skipSequence && !_skipReason.IsSilent() ? _skipReason : SkipReason.None;
}
