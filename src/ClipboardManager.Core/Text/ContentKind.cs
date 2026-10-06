namespace ClipboardManager.Core.Text;

public enum ContentKind
{
    Text,
    Url,
    Path,
}

/// <summary>
/// Deliberately strict classification, only used for an icon. Computed on load, never stored,
/// so a rule change needs no migration.
/// </summary>
public static class ContentClassifier
{
    public static ContentKind Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty || trimmed.Length > 2048 || trimmed.IndexOfAny("\r\n") >= 0)
        {
            return ContentKind.Text;
        }

        if (IsUrl(trimmed))
        {
            return ContentKind.Url;
        }

        return IsPath(trimmed) ? ContentKind.Path : ContentKind.Text;
    }

    private static bool IsUrl(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(text.ToString(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https" or "ftp" or "mailto" && (uri.Scheme == "mailto" || uri.Host.Length > 0);
    }

    private static bool IsPath(ReadOnlySpan<char> text)
    {
        if (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/')
        {
            return true;
        }

        if (text.Length > 2 && text.StartsWith(@"\\") && char.IsLetterOrDigit(text[2]))
        {
            return true;
        }

        return text.Length > 2 && text[0] == '~' && text[1] is '/' or '\\';
    }
}
