using System.Buffers.Binary;
using ClipboardManager.Core.Capture;

namespace ClipboardManager.Clipboard;

internal enum ClipboardImageFormat
{
    /// <summary>The registered "PNG" format (browsers, Office, Snipping Tool): lossless, with transparency.</summary>
    Png,

    /// <summary>CF_DIB: a BITMAPINFOHEADER (or a V4/V5 header) and the pixels; Windows synthesizes it from CF_BITMAP and CF_DIBV5.</summary>
    Dib,
}

/// <summary>
/// Where the pixels of a CF_DIB are and how to read them. Clipboard data is untrusted: every offset
/// and size is checked against the bytes that are actually there, in 64-bit arithmetic.
/// </summary>
internal readonly record struct DibLayout(
    int Width,
    int Height,
    bool TopDown,
    int BitCount,
    uint Compression,
    long PixelOffset,
    long Stride,
    bool StandardMasks,
    bool AlphaAllowed)
{
    public const int FileHeaderSize = 14;
    public const int InfoHeaderSize = 40;
    public const uint BiRgb = 0;
    public const uint BiRle8 = 1;
    public const uint BiRle4 = 2;
    public const uint BiBitFields = 3;
    public const uint BiAlphaBitFields = 6;

    private const uint RedMask = 0x00FF0000;
    private const uint GreenMask = 0x0000FF00;
    private const uint BlueMask = 0x000000FF;
    private const uint AlphaMask = 0xFF000000;

    /// <summary>24- and 32-bit layouts without compression, read directly; everything else goes through the BMP decoder.</summary>
    public bool IsDirect => Compression == BiRgb ? BitCount is 24 or 32 : BitCount == 32 && StandardMasks;

    public static SkipReason Parse(ReadOnlySpan<byte> dib, out DibLayout layout)
    {
        layout = default;
        if (dib.Length < InfoHeaderSize)
        {
            return SkipReason.NotText;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize is not (40 or 52 or 56 or 108 or 124) || dib.Length < headerSize)
        {
            return SkipReason.NotText;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (width <= 0 || rawHeight is 0 or int.MinValue)
        {
            return SkipReason.NotText;
        }

        // Before anything else: a huge header must not cost more than these few bytes.
        var height = Math.Abs(rawHeight);
        if (!ImageLimits.Fits(width, height))
        {
            return SkipReason.TooLarge;
        }

        var rle = compression is BiRle8 or BiRle4;
        var bitFields = compression is BiBitFields or BiAlphaBitFields;
        if (bitCount is not (1 or 4 or 8 or 16 or 24 or 32) ||
            !(compression == BiRgb || rle || bitFields) ||
            (rle && (rawHeight < 0 || bitCount != (compression == BiRle8 ? 8 : 4))) ||
            (bitFields && bitCount is not (16 or 32)) ||
            (compression == BiAlphaBitFields && headerSize is not (InfoHeaderSize or >= 56)) ||
            colorsUsed > 256)
        {
            return SkipReason.NotText;
        }

        // With a plain BITMAPINFOHEADER the color masks follow the header; larger headers contain them.
        var maskBytes = headerSize == InfoHeaderSize ? compression switch
        {
            BiBitFields => 12,
            BiAlphaBitFields => 16,
            _ => 0,
        } : 0;
        if (dib.Length < headerSize + maskBytes)
        {
            return SkipReason.NotText;
        }

        uint red = RedMask, green = GreenMask, blue = BlueMask, alpha = 0;
        if (bitFields || headerSize >= 52)
        {
            red = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
            green = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
            blue = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
        }

        if (headerSize >= 56 || compression == BiAlphaBitFields)
        {
            alpha = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
        }

        // Bitmaps with up to 8 bits per pixel always have a palette; for more it is an optional
        // "optimization" palette that only has to be skipped.
        long paletteEntries = bitCount <= 8 && colorsUsed == 0 ? 1L << bitCount : colorsUsed;
        var pixelOffset = headerSize + maskBytes + (paletteEntries * 4);
        var stride = ((((long)width * bitCount) + 31) / 32) * 4;
        var imageSize = rle ? 0 : stride * height;
        if (dib.Length < pixelOffset + Math.Max(imageSize, 1))
        {
            return SkipReason.NotText;
        }

        var standardMasks = !bitFields || (red == RedMask && green == GreenMask && blue == BlueMask);
        if (headerSize >= 108 && compression == BiBitFields && dib.Length >= pixelOffset + 12 + imageSize &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib[(int)pixelOffset..]) == red &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib[((int)pixelOffset + 4)..]) == green &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib[((int)pixelOffset + 8)..]) == blue)
        {
            // Some producers repeat the masks of a V4/V5 header after it, as if it were a BITMAPINFOHEADER.
            pixelOffset += 12;
        }

        // 32-bit pixels carry alpha only when the producer can mean it: a BITMAPINFOHEADER with BI_RGB
        // (the de-facto convention of browsers) or an explicit alpha mask. Bitmaps synthesized from a
        // GDI CF_BITMAP use BI_BITFIELDS without one; their fourth byte is undefined.
        var alphaAllowed = bitCount == 32 && standardMasks &&
            ((headerSize == InfoHeaderSize && compression == BiRgb) || alpha == AlphaMask);

        layout = new DibLayout(width, height, rawHeight < 0, bitCount, compression, pixelOffset, stride, standardMasks, alphaAllowed);
        return SkipReason.None;
    }
}

/// <summary>The size check of a PNG, made on its first 33 bytes before it is copied.</summary>
internal static class PngHeader
{
    public const int MinLength = 33;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> HeaderChunk => "IHDR"u8;

    public static SkipReason Check(ReadOnlySpan<byte> png)
    {
        if (png.Length < MinLength || !png.StartsWith(Signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13 || !png.Slice(12, 4).SequenceEqual(HeaderChunk))
        {
            return SkipReason.NotText;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
        if (width == 0 || height == 0)
        {
            return SkipReason.NotText;
        }

        return ImageLimits.Fits(width, height) ? SkipReason.None : SkipReason.TooLarge;
    }
}
