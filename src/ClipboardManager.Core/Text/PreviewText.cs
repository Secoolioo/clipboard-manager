using System.Text;

namespace ClipboardManager.Core.Text;

/// <summary>Single-line row previews that make otherwise invisible differences visible.</summary>
public static class PreviewText
{
    public const char NewlineMarker = '⏎';
    public const char SpaceMarker = '·';
    public const int DefaultMaxLength = 160;

    /// <summary>
    /// First non-blank line, whitespace runs collapsed, leading/trailing spaces of a single-line text
    /// shown as <see cref="SpaceMarker"/>, a trailing line break as <see cref="NewlineMarker"/>.
    /// </summary>
    public static string ForRow(string text, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var span = text.AsSpan();
        var endsWithNewline = span.EndsWith("\n") || span.EndsWith("\r");
        var firstLine = FirstNonBlankLine(span, out var isMultiline);

        var builder = new StringBuilder(Math.Min(firstLine.Length, maxLength) + 4);
        if (!isMultiline)
        {
            var leading = CountEdgeWhitespace(firstLine, fromStart: true);
            if (leading == firstLine.Length)
            {
                return new string(SpaceMarker, Math.Min(leading, maxLength));
            }

            var trailing = CountEdgeWhitespace(firstLine, fromStart: false);
            builder.Append(SpaceMarker, Math.Min(leading, 3));
            AppendCollapsed(builder, firstLine[leading..^trailing], maxLength + 1);
            if (builder.Length <= maxLength)
            {
                builder.Append(SpaceMarker, Math.Min(trailing, 3));
            }
        }
        else
        {
            // Multi-line: everything from the first non-blank line on, joined into one line, so
            // "{" or "Best regards," alone do not hide what the entry is.
            var start = span.IndexOf(firstLine);
            AppendCollapsed(builder, span[Math.Max(0, start)..].Trim(), maxLength + 1);
        }

        Truncate(builder, maxLength);

        if (endsWithNewline && !isMultiline)
        {
            builder.Append(' ').Append(NewlineMarker);
        }

        return builder.ToString();
    }

    /// <summary>A window of the text around <paramref name="matchIndex"/> for search results.</summary>
    public static string Snippet(string text, int matchIndex, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (matchIndex <= 40)
        {
            return ForRow(text, maxLength);
        }

        var start = Math.Max(0, matchIndex - 30);
        if (start > 0 && char.IsLowSurrogate(text[start]))
        {
            start--; // never start in the middle of an emoji
        }

        var window = text.AsSpan(start, Math.Min(text.Length - start, maxLength * 2));
        var builder = new StringBuilder(maxLength + 2).Append('…');
        AppendCollapsed(builder, window, maxLength + 1);
        Truncate(builder, maxLength);
        return builder.ToString();
    }

    /// <summary>Cuts to <paramref name="maxLength"/> characters including the ellipsis, without splitting a surrogate pair.</summary>
    private static void Truncate(StringBuilder builder, int maxLength)
    {
        if (builder.Length <= maxLength)
        {
            return;
        }

        var length = maxLength - 1;
        if (length > 0 && char.IsHighSurrogate(builder[length - 1]))
        {
            length--;
        }

        builder.Length = length;
        builder.Append('…');
    }

    private static ReadOnlySpan<char> FirstNonBlankLine(ReadOnlySpan<char> text, out bool isMultiline)
    {
        isMultiline = false;
        var rest = text;
        while (true)
        {
            var end = rest.IndexOfAny('\r', '\n');
            var line = end < 0 ? rest : rest[..end];
            var remaining = end < 0 ? [] : SkipLineBreak(rest[end..]);

            if (!line.IsWhiteSpace() || remaining.IsEmpty)
            {
                isMultiline = !remaining.TrimEnd().IsEmpty;
                return line;
            }

            rest = remaining;
        }
    }

    private static ReadOnlySpan<char> SkipLineBreak(ReadOnlySpan<char> text) =>
        text.StartsWith("\r\n") ? text[2..] : text[1..];

    private static int CountEdgeWhitespace(ReadOnlySpan<char> line, bool fromStart)
    {
        var count = 0;
        if (fromStart)
        {
            while (count < line.Length && char.IsWhiteSpace(line[count]))
            {
                count++;
            }
        }
        else
        {
            while (count < line.Length && char.IsWhiteSpace(line[line.Length - 1 - count]))
            {
                count++;
            }
        }

        return count;
    }

    private static void AppendCollapsed(StringBuilder builder, ReadOnlySpan<char> text, int maxLength)
    {
        var previousWasSpace = false;
        foreach (var c in text)
        {
            if (builder.Length >= maxLength)
            {
                return;
            }

            if (c is '\r' or '\n')
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
                continue;
            }

            builder.Append(char.IsControl(c) ? '�' : c);
            previousWasSpace = false;
        }
    }
}
