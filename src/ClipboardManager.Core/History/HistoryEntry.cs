using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClipboardManager.Core.History;

/// <summary>
/// In-memory view of one history entry. <see cref="SearchText"/> is the full text for entries up to
/// <see cref="HistoryStore.SearchHeadLength"/> characters, otherwise only its beginning.
/// </summary>
public sealed record HistoryEntry(
    long Id,
    string SearchText,
    int CharCount,
    int LineCount,
    long CreatedAtMs,
    long LastUsedAtMs,
    long? PinnedAtMs)
{
    public bool IsPinned => PinnedAtMs is not null;

    /// <summary>True when only the beginning of the text is searchable.</summary>
    public bool IsPartiallySearchable => CharCount > SearchText.Length;
}

/// <summary>A deleted entry kept in memory so the popup can undo the deletion.</summary>
public sealed record DeletedEntry(HistoryEntry Entry, string Text);

public static class ContentHash
{
    /// <summary>SHA-256 over the UTF-16 code units (no extra encoding copy for large texts).</summary>
    public static byte[] Compute(string text) => SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan()));
}

public static class TextMetrics
{
    public static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var lines = 1;
        var span = text.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= span.Length || span[i + 1] != '\n')))
            {
                // A trailing line break does not start a new visible line.
                if (i + 1 < span.Length)
                {
                    lines++;
                }
            }
        }

        return lines;
    }
}
