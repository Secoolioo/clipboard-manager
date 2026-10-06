namespace ClipboardManager.Core.Capture;

/// <summary>Why a clipboard change was not stored. Values without a user-visible reason are marked silent.</summary>
public enum SkipReason
{
    None,

    /// <summary>Silent: our own write.</summary>
    OwnWrite,

    /// <summary>Silent: the clipboard was emptied.</summary>
    Cleared,

    /// <summary>Silent: empty or whitespace-only text.</summary>
    Blank,

    Paused,
    IgnoredOnce,

    /// <summary>The source app marked the content as not-for-history (password managers, private windows).</summary>
    MarkedBySource,

    ExcludedApp,
    LooksLikeSecret,
    TooLarge,
    NotText,
    ReadFailed,
}

public static class SkipReasons
{
    public static bool IsSilent(this SkipReason reason) =>
        reason is SkipReason.None or SkipReason.OwnWrite or SkipReason.Cleared or SkipReason.Blank;
}

/// <summary>What the clipboard reader saw, read entirely inside one OpenClipboard session.</summary>
public sealed record ClipboardSnapshot(uint Sequence, SkipReason ReaderSkip, string? Text, string? SourceExe);

public sealed record CaptureSettings(bool SkipDetectedSecrets, IReadOnlyCollection<string> ExcludedApps)
{
    public static readonly CaptureSettings Default = new(true, []);

    public bool IsExcluded(string? exe)
    {
        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        foreach (var excluded in ExcludedApps)
        {
            if (string.Equals(excluded, exe, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Final decision after the reader: content checks that need the text.</summary>
public static class CapturePolicy
{
    public const int MaxCaptureChars = 256 * 1024;

    public static SkipReason Evaluate(ClipboardSnapshot snapshot, CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        if (snapshot.ReaderSkip != SkipReason.None)
        {
            return snapshot.ReaderSkip;
        }

        if (snapshot.Text is null)
        {
            return SkipReason.NotText;
        }

        if (settings.IsExcluded(snapshot.SourceExe))
        {
            return SkipReason.ExcludedApp;
        }

        if (snapshot.Text.Length > MaxCaptureChars)
        {
            return SkipReason.TooLarge;
        }

        if (string.IsNullOrWhiteSpace(snapshot.Text))
        {
            return SkipReason.Blank;
        }

        if (settings.SkipDetectedSecrets && SecretDetector.LooksLikeSecret(snapshot.Text))
        {
            return SkipReason.LooksLikeSecret;
        }

        return SkipReason.None;
    }
}
