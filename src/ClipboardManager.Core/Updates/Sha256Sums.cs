namespace ClipboardManager.Core.Updates;

/// <summary>
/// Reads a SHA256SUMS.txt in the format of <c>sha256sum</c>: "&lt;64 hex&gt;  &lt;name&gt;" (text
/// mode) or "&lt;64 hex&gt; *&lt;name&gt;" (binary mode), one file per line.
/// </summary>
public static class Sha256Sums
{
    /// <summary>File name → lower-case hex hash. Malformed lines are skipped; a name listed with two different hashes is dropped.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.TrimStart('﻿').Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 67 || !line.AsSpan(0, 64).ContainsOnlyHex() || line[64] != ' ' || line[65] is not (' ' or '*'))
            {
                continue;
            }

            var name = line[66..];
            var hash = line[..64].ToLowerInvariant();
            if (name.Trim().Length == 0)
            {
                continue;
            }

            if (sums.TryGetValue(name, out var existing) && existing != hash)
            {
                conflicting.Add(name);
            }

            sums[name] = hash;
        }

        foreach (var name in conflicting)
        {
            sums.Remove(name);
        }

        return sums;
    }

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
