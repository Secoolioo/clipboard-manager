using System.Runtime.CompilerServices;
using System.Windows.Media;
using ClipboardManager.Clipboard;

namespace ClipboardManager.Popup;

/// <summary>
/// Decoded list thumbnails, keyed by the thumbnail bytes of an index entry: decoded on first use
/// (rows that are actually shown), kept while the entry exists and collected together with it.
/// </summary>
internal static class Thumbnails
{
    private static readonly ConditionalWeakTable<byte[], Decoded> Cache = new();

    public static ImageSource? Get(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        return png.Length == 0 ? null : Cache.GetValue(png, static bytes => new Decoded(ImageCodec.DecodeForDisplay(bytes, 0, 0))).Image;
    }

    private sealed class Decoded(ImageSource? image)
    {
        public ImageSource? Image { get; } = image;
    }
}
