using System.Globalization;
using ClipboardManager.Core.History;

namespace ClipboardManager.Core.Search;

/// <summary>
/// Full scan on every keystroke: AND across whitespace-separated terms, ordinal and
/// case-insensitive. A few thousand entries take well under a millisecond, so there is no index
/// and no incremental state to keep consistent. Images are found by a word for "image" in the UI
/// language and by their size in pixels.
/// </summary>
public static class HistorySearch
{
    public static string[] ParseTerms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static HistorySnapshot Filter(HistorySnapshot source, string? query, string imageWord = "")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(imageWord);
        var terms = ParseTerms(query);
        if (terms.Length == 0)
        {
            return source;
        }

        return new HistorySnapshot(Match(source.Pinned, terms, imageWord), Match(source.History, terms, imageWord));
    }

    /// <summary>What a query is matched against: the text, or for an image the word for "image" and its size.</summary>
    public static string SearchableText(HistoryEntry entry, string imageWord)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Image is { } image
            ? string.Create(CultureInfo.InvariantCulture, $"{imageWord} {image.Width} × {image.Height} {image.Width}x{image.Height}")
            : entry.SearchText;
    }

    public static bool Matches(string text, string[] terms)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(terms);
        foreach (var term in terms)
        {
            if (!text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Index of the first term's first occurrence (for snippets), or -1.</summary>
    public static int FirstMatchIndex(string text, string[] terms)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(terms);
        var best = -1;
        foreach (var term in terms)
        {
            var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }

        return best;
    }

    private static List<HistoryEntry> Match(IReadOnlyList<HistoryEntry> entries, string[] terms, string imageWord)
    {
        var result = new List<HistoryEntry>();
        foreach (var entry in entries)
        {
            if (Matches(SearchableText(entry, imageWord), terms))
            {
                result.Add(entry);
            }
        }

        return result;
    }
}
