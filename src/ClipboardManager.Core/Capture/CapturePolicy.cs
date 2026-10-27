using ClipboardManager.Core.History;

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

    /// <summary>Neither text nor a supported image (files, other formats, an unreadable picture).</summary>
    NotText,
    ReadFailed,

    /// <summary>An image was copied while saving images is switched off.</summary>
    ImagesOff,
}

public static class SkipReasons
{
    public static bool IsSilent(this SkipReason reason) =>
        reason is SkipReason.None or SkipReason.OwnWrite or SkipReason.Cleared or SkipReason.Blank;
}

/// <summary>
/// What the clipboard reader saw, read entirely inside one OpenClipboard session. An image is
/// normalized after the clipboard was released; <see cref="Image"/> is set only for that case.
/// </summary>
public sealed record ClipboardSnapshot(uint Sequence, SkipReason ReaderSkip, string? Text, string? SourceExe, CapturedImage? Image = null);

public sealed record CaptureSettings(bool SkipDetectedSecrets, IReadOnlyCollection<string> ExcludedApps, bool CaptureImages = true)
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

        if (snapshot.Image is not null)
        {
            if (settings.IsExcluded(snapshot.SourceExe))
            {
                return SkipReason.ExcludedApp;
            }

            return settings.CaptureImages ? SkipReason.None : SkipReason.ImagesOff;
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

/// <summary>Bounds for clipboard images, checked on the header before any pixel data is copied.</summary>
public static class ImageLimits
{
    /// <summary>Longest side in pixels.</summary>
    public const int MaxSide = 16384;

    /// <summary>25 megapixels: three 4K monitors in one screenshot still fit.</summary>
    public const long MaxPixels = 25L * 1024 * 1024;

    /// <summary>Largest clipboard format read into the process (an uncompressed 32-bit image of <see cref="MaxPixels"/> plus headers).</summary>
    public const long MaxClipboardBytes = (MaxPixels * 4) + (1024 * 1024);

    /// <summary>Largest normalized PNG kept in the history.</summary>
    public const long MaxStoredBytes = 32L * 1024 * 1024;

    /// <summary>Thumbnail box in pixels (twice the 48×28 slot in the list, for high-DPI screens).</summary>
    public const int ThumbnailWidth = 96;

    public const int ThumbnailHeight = 56;

    public static bool Fits(long width, long height) =>
        width is > 0 and <= MaxSide && height is > 0 and <= MaxSide && width * height <= MaxPixels;
}
