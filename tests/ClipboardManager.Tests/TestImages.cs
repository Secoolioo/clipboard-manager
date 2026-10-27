using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Clipboard;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;

namespace ClipboardManager.Tests;

/// <summary>Pictures for tests, built in code: neutral demo data, never real clipboard content.</summary>
internal static class TestImages
{
    public const uint BiRgb = 0;
    public const uint BiBitFields = 3;

    /// <summary>A stored image as the store sees it: the codec is not involved, so any unique bytes do.</summary>
    public static CapturedImage Fake(int length = 4096, int width = 640, int height = 480) =>
        new(Canary.CreateBytes(length), Canary.CreateBytes(64), width, height);

    /// <summary>A CF_DIB: header, optional masks or palette (<paramref name="extra"/>), then the rows (bottom-up unless top-down).</summary>
    public static byte[] Dib(
        int width,
        int height,
        int bitCount,
        Func<int, int, (byte B, byte G, byte R, byte A)> pixel,
        bool topDown = false,
        int headerSize = 40,
        uint compression = BiRgb,
        byte[]? extra = null,
        uint colorsUsed = 0)
    {
        extra ??= [];
        var stride = (((width * bitCount) + 31) / 32) * 4;
        var offset = headerSize + extra.Length;
        var dib = new byte[offset + (stride * height)];
        BinaryPrimitives.WriteInt32LittleEndian(dib, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), topDown ? -height : height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), (ushort)bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), colorsUsed);
        extra.CopyTo(dib, headerSize);
        var bytesPerPixel = Math.Max(1, bitCount / 8);
        for (var y = 0; y < height; y++)
        {
            var row = offset + ((topDown ? y : height - 1 - y) * stride);
            for (var x = 0; x < width; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                byte[] channels = [b, g, r, a];
                channels.AsSpan(0, bytesPerPixel).CopyTo(dib.AsSpan(row + (x * bytesPerPixel)));
            }
        }

        return dib;
    }

    /// <summary>A PNG from straight BGRA pixels, the way a browser puts one on the clipboard.</summary>
    public static byte[] Png(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel, double dpi = 96)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                var i = ((y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
            }
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A copy of <paramref name="png"/> with a tEXt chunk right after the header.</summary>
    public static byte[] WithTextChunk(byte[] png, string keyword, string text)
    {
        var data = System.Text.Encoding.Latin1.GetBytes(keyword + "\0" + text);
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        const int AfterHeader = 8 + 25;
        return [.. png.AsSpan(0, AfterHeader), .. chunk, .. png.AsSpan(AfterHeader)];
    }

    /// <summary>Size and straight BGRA pixels of an encoded image.</summary>
    public static (int Width, int Height, byte[] Bgra) Pixels(byte[] encoded)
    {
        using var stream = new MemoryStream(encoded);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return (bgra.PixelWidth, bgra.PixelHeight, pixels);
    }

    /// <summary>The PNG color type from IHDR: 2 = RGB (opaque), 6 = RGBA.</summary>
    public static int ColorType(byte[] png) => png[25];

    /// <summary>Runs the clipboard codec on the bytes of one clipboard format.</summary>
    public static (SkipReason Reason, CapturedImage? Image) Normalize(ClipboardImageFormat format, byte[] data)
    {
        using var clip = ClipboardImageData.FromSpan(format, data);
        var reason = ImageCodec.Normalize(clip, out var image);
        return (reason, image);
    }

    /// <summary>A normalized image of a simple pattern, with transparency if asked for.</summary>
    public static CapturedImage Captured(int width, int height, bool transparent = false)
    {
        var (reason, image) = Normalize(ClipboardImageFormat.Png, Png(width, height, (x, y) =>
            ((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)), 128, transparent && x == 0 ? (byte)0 : (byte)255)));
        Assert.Equal(SkipReason.None, reason);
        return image!;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
