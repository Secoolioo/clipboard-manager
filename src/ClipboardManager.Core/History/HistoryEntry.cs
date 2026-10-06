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
    /// <summary>
    /// SQLite stores text as UTF-8, which cannot represent unpaired surrogates (invalid UTF-16 that
    /// some apps put on the clipboard). Normalizing them to U+FFFD before hashing keeps the stored
    /// text, its hash and later re-copies consistent. Valid text is returned unchanged (same instance).
    /// </summary>
    public static string NormalizeSurrogates(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            if (char.IsHighSurrogate(span[i]) && i + 1 < span.Length && char.IsLowSurrogate(span[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(span[i]))
            {
                return string.Create(text.Length, text, static (buffer, source) =>
                {
                    for (var j = 0; j < source.Length; j++)
                    {
                        var c = source[j];
                        if (char.IsHighSurrogate(c) && j + 1 < source.Length && char.IsLowSurrogate(source[j + 1]))
                        {
                            buffer[j] = c;
                            buffer[j + 1] = source[j + 1];
                            j++;
                        }
                        else
                        {
                            buffer[j] = char.IsSurrogate(c) ? '�' : c;
                        }
                    }
                });
            }
        }

        return text;
    }

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
