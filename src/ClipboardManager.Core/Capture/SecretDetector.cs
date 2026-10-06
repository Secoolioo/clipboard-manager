using System.Text.RegularExpressions;

namespace ClipboardManager.Core.Capture;

/// <summary>
/// Recognizes only structurally unambiguous credentials (fixed prefixes, exact lengths). Passwords
/// and generic high-entropy strings are intentionally not detected: a filter that silently drops
/// legitimate text is worse than none.
/// </summary>
public static partial class SecretDetector
{
    public static bool LooksLikeSecret(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Pattern().IsMatch(text);
    }

    [GeneratedRegex(
        """
        -----BEGIN\ (?:RSA\ |EC\ |DSA\ |OPENSSH\ |ENCRYPTED\ |PGP\ )?PRIVATE\ KEY(?:\ BLOCK)?-----
        | \b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}\b
        | \bgithub_pat_[A-Za-z0-9_]{82}\b
        | \b(?:AKIA|ASIA)[A-Z0-9]{16}\b
        | \bxox[abprs]-[A-Za-z0-9-]{10,}
        | \b(?:sk|rk)_live_[A-Za-z0-9]{24,}\b
        | \bglpat-[A-Za-z0-9_-]{20,}\b
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex Pattern();
}
