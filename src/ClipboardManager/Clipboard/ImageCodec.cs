using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;

namespace ClipboardManager.Clipboard;

/// <summary>
/// One clipboard image format, copied into native memory inside the clipboard session and freed
/// deterministically (a 4K bitmap is 33 MB that should not linger on the managed heap). Room for a
/// BITMAPFILEHEADER is reserved in front, so a DIB can go to the BMP decoder without another copy.
/// </summary>
internal sealed unsafe class ClipboardImageData : IDisposable
{
    private byte* _buffer;

    private ClipboardImageData(ClipboardImageFormat format, byte* buffer, int length)
    {
        Format = format;
        _buffer = buffer;
        Length = length;
    }

    public ClipboardImageFormat Format { get; }

    public int Length { get; }

    /// <summary>The clipboard bytes (our own copy: the codec may change them).</summary>
    public Span<byte> Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer == null, this);
            return new Span<byte>(_buffer + DibLayout.FileHeaderSize, Length);
        }
    }

    /// <summary>The reserved file header, followed by <see cref="Data"/>.</summary>
    public Span<byte> WithFileHeader
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer == null, this);
            return new Span<byte>(_buffer, Length + DibLayout.FileHeaderSize);
        }
    }

    public static ClipboardImageData Copy(ClipboardImageFormat format, IntPtr source, int length) =>
        FromSpan(format, new ReadOnlySpan<byte>((void*)source, length));

    public static ClipboardImageData FromSpan(ClipboardImageFormat format, ReadOnlySpan<byte> data)
    {
        var buffer = (byte*)NativeMemory.Alloc((nuint)data.Length + DibLayout.FileHeaderSize);
        data.CopyTo(new Span<byte>(buffer + DibLayout.FileHeaderSize, data.Length));
        return new ClipboardImageData(format, buffer, data.Length);
    }

    public void Dispose()
    {
        NativeMemory.Free(_buffer);
        _buffer = null;
    }
}

/// <summary>
/// Turns clipboard images into what the history stores – a PNG at 96 DPI without any metadata,
/// 24-bit when the picture is opaque, plus a small thumbnail – and back into clipboard formats.
/// Only WPF imaging (WIC), which works on any thread: nothing here touches the UI, and every image
/// returned for display is frozen.
/// </summary>
internal static class ImageCodec
{
    private const double Dpi = 96;
    private const int StripBytes = 64 * 1024;

    public static SkipReason CheckHeader(ClipboardImageFormat format, ReadOnlySpan<byte> data) =>
        format == ClipboardImageFormat.Png ? PngHeader.Check(data) : DibLayout.Parse(data, out _);

    /// <summary>Normalizes one clipboard image. Frees <paramref name="data"/> as soon as its pixels are copied out.</summary>
    public static SkipReason Normalize(ClipboardImageData data, out CapturedImage? image)
    {
        ArgumentNullException.ThrowIfNull(data);
        image = null;
        try
        {
            var reason = Decode(data, out var pixels);
            data.Dispose();
            if (pixels is null)
            {
                return reason;
            }

            BitmapSource normalized;
            using (pixels)
            {
                normalized = pixels.ToBitmapSource();
            }

            var png = Encode(normalized);
            if (png.LongLength > ImageLimits.MaxStoredBytes)
            {
                return SkipReason.TooLarge;
            }

            var (width, height) = (normalized.PixelWidth, normalized.PixelHeight);
            image = new CapturedImage(png, Thumbnail(png, width, height), width, height);
            return SkipReason.None;
        }
        catch (OutOfMemoryException)
        {
            return SkipReason.TooLarge;
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            return SkipReason.NotText;
        }
        finally
        {
            data.Dispose();
        }
    }

    /// <summary>
    /// A 24-bit bottom-up CF_DIB of a stored PNG, transparent areas on white: apps that only read
    /// bitmaps (Paint, older programs) get the picture as it looks, not black where it was see-through.
    /// </summary>
    public static byte[]? TryCreateDib(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        try
        {
            var source = new FormatConvertedBitmap(DecodeFrame(png), PixelFormats.Bgra32, null, 0);
            var (width, height) = (source.PixelWidth, source.PixelHeight);
            var stride = (((width * 24) + 31) / 32) * 4;

            // Zeroed on purpose: the row padding must not hand stale heap bytes to other apps.
            var dib = new byte[DibLayout.InfoHeaderSize + ((long)stride * height)];
            BinaryPrimitives.WriteInt32LittleEndian(dib, DibLayout.InfoHeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), stride * height);

            var rowsPerStrip = Math.Max(1, StripBytes / (width * 4));
            var strip = new byte[rowsPerStrip * width * 4];
            for (var top = 0; top < height; top += rowsPerStrip)
            {
                var rows = Math.Min(rowsPerStrip, height - top);
                source.CopyPixels(new Int32Rect(0, top, width, rows), strip, width * 4, 0);
                for (var row = 0; row < rows; row++)
                {
                    var from = strip.AsSpan(row * width * 4, width * 4);
                    var to = dib.AsSpan(DibLayout.InfoHeaderSize + ((height - 1 - top - row) * stride), width * 3);
                    for (var x = 0; x < width; x++)
                    {
                        var alpha = from[(x * 4) + 3];
                        to[x * 3] = OnWhite(from[x * 4], alpha);
                        to[(x * 3) + 1] = OnWhite(from[(x * 4) + 1], alpha);
                        to[(x * 3) + 2] = OnWhite(from[(x * 4) + 2], alpha);
                    }
                }
            }

            return dib;
        }
        catch (Exception ex) when (ex is OutOfMemoryException || IsDecodeFailure(ex))
        {
            return null;
        }
    }

    /// <summary>A frozen image for display, scaled down so its longer side is at most <paramref name="maxSide"/> pixels (0: original size).</summary>
    public static BitmapSource? DecodeForDisplay(byte[] png, int width, int height, int maxSide = 0)
    {
        ArgumentNullException.ThrowIfNull(png);
        try
        {
            if (maxSide <= 0 || Math.Max(width, height) <= maxSide)
            {
                return DecodeScaled(png, 0, 0);
            }

            // Only one side is given, so the decoder keeps the aspect ratio.
            return width >= height ? DecodeScaled(png, maxSide, 0) : DecodeScaled(png, 0, maxSide);
        }
        catch (Exception ex) when (ex is OutOfMemoryException || IsDecodeFailure(ex))
        {
            return null;
        }
    }

    private static SkipReason Decode(ClipboardImageData data, out Pixels? pixels)
    {
        pixels = null;
        if (data.Format == ClipboardImageFormat.Png)
        {
            var check = PngHeader.Check(data.Data);
            return check == SkipReason.None ? DecodeEncoded(data.Data, isDib: false, out pixels) : check;
        }

        var reason = DibLayout.Parse(data.Data, out var layout);
        if (reason != SkipReason.None)
        {
            return reason;
        }

        if (layout.IsDirect)
        {
            pixels = FromDirectDib(data.Data, layout);
            return SkipReason.None;
        }

        // Palettes, 16-bit, RLE and unusual masks: hand it to the BMP decoder as a .bmp file.
        var file = data.WithFileHeader;
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file[2..], (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file[10..], (uint)(DibLayout.FileHeaderSize + layout.PixelOffset));
        return DecodeEncoded(file, isDib: true, out pixels);
    }

    private static Pixels FromDirectDib(ReadOnlySpan<byte> dib, DibLayout layout)
    {
        var source = dib.Slice((int)layout.PixelOffset, (int)(layout.Stride * layout.Height));
        var opaque = layout.BitCount == 24 || !layout.AlphaAllowed || HasNoUsableAlpha(source);
        var pixels = new Pixels(layout.Width, layout.Height, alpha: !opaque);
        try
        {
            // DIBs are usually stored bottom-up; the normalized pixels are always top-down.
            var sameLayout = layout.BitCount == 24 || !opaque;
            for (var y = 0; y < layout.Height; y++)
            {
                var from = source.Slice((int)((layout.TopDown ? y : layout.Height - 1 - y) * layout.Stride), (int)layout.Stride);
                var to = pixels.Row(y);
                if (sameLayout)
                {
                    from[..(layout.Width * (layout.BitCount / 8))].CopyTo(to);
                    continue;
                }

                for (var x = 0; x < layout.Width; x++)
                {
                    to[x * 3] = from[x * 4];
                    to[(x * 3) + 1] = from[(x * 4) + 1];
                    to[(x * 3) + 2] = from[(x * 4) + 2];
                }
            }
        }
        catch
        {
            pixels.Dispose();
            throw;
        }

        return pixels;
    }

    private static unsafe SkipReason DecodeEncoded(Span<byte> bytes, bool isDib, out Pixels? pixels)
    {
        pixels = null;
        fixed (byte* pointer = bytes)
        {
            using var stream = new UnmanagedMemoryStream(pointer, bytes.Length);
            var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames;
            if (frames.Count == 0)
            {
                return SkipReason.NotText;
            }

            var frame = frames[0];
            if (!ImageLimits.Fits(frame.PixelWidth, frame.PixelHeight))
            {
                return SkipReason.TooLarge;
            }

            var result = new Pixels(frame.PixelWidth, frame.PixelHeight, alpha: true);
            try
            {
                new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0)
                    .CopyPixels(new Int32Rect(0, 0, result.Width, result.Height), (IntPtr)result.Buffer, (int)result.Size, result.Stride);
                if (result.IsOpaque(allTransparentIsOpaque: isDib))
                {
                    result.DropAlpha();
                }
            }
            catch
            {
                result.Dispose();
                throw;
            }

            pixels = result;
            return SkipReason.None;
        }
    }

    /// <summary>True when every alpha byte is 0 (an app that never wrote alpha) or every one is 255.</summary>
    private static bool HasNoUsableAlpha(ReadOnlySpan<byte> bgra)
    {
        var (anyVisible, anySeeThrough) = (false, false);
        for (var i = 3; i < bgra.Length; i += 4)
        {
            anyVisible |= bgra[i] != 0;
            anySeeThrough |= bgra[i] != 255;
            if (anyVisible && anySeeThrough)
            {
                return false;
            }
        }

        return true;
    }

    private static byte OnWhite(byte channel, byte alpha) => (byte)(((channel * alpha) + (255 * (255 - alpha)) + 127) / 255);

    private static BitmapFrame DecodeFrame(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static BitmapImage DecodeScaled(byte[] png, int width, int height)
    {
        using var stream = new MemoryStream(png, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = stream;
        if (width > 0)
        {
            image.DecodePixelWidth = width;
        }

        if (height > 0)
        {
            image.DecodePixelHeight = height;
        }

        image.EndInit();
        image.Freeze();
        return image;
    }

    private static byte[] Thumbnail(byte[] png, int width, int height)
    {
        var scale = Math.Min(1.0, Math.Min((double)ImageLimits.ThumbnailWidth / width, (double)ImageLimits.ThumbnailHeight / height));
        var thumbnailWidth = Math.Max(1, (int)Math.Round(width * scale));
        var thumbnailHeight = Math.Max(1, (int)Math.Round(height * scale));
        return Encode(DecodeScaled(png, thumbnailWidth, thumbnailHeight));
    }

    private static byte[] Encode(BitmapSource source)
    {
        // A frame created from plain pixels: nothing of the producer's metadata (text chunks, EXIF, profiles) can survive.
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool IsDecodeFailure(Exception ex) =>
        ex is NotSupportedException or FormatException or ArgumentException or InvalidOperationException or OverflowException or ExternalException or IOException;

    /// <summary>Normalized top-down pixels (Bgra32, or Bgr24 once the alpha is dropped) in native memory.</summary>
    private sealed unsafe class Pixels : IDisposable
    {
        public Pixels(int width, int height, bool alpha)
        {
            Width = width;
            Height = height;
            Format = alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
            Stride = StrideOf(width, alpha);
            Size = (long)Stride * height;
            Buffer = (byte*)NativeMemory.AllocZeroed((nuint)Size);
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format { get; private set; }

        public int Stride { get; private set; }

        public long Size { get; private set; }

        public byte* Buffer { get; private set; }

        public Span<byte> Row(int y) => new(Buffer + ((long)y * Stride), Stride);

        public bool IsOpaque(bool allTransparentIsOpaque)
        {
            var bgra = new ReadOnlySpan<byte>(Buffer, (int)Size);
            return allTransparentIsOpaque ? HasNoUsableAlpha(bgra) : AllAlphaOpaque(bgra);
        }

        /// <summary>Bgra32 to Bgr24 in place: every write lands at or before the pixel it came from.</summary>
        public void DropAlpha()
        {
            var stride = StrideOf(Width, alpha: false);
            for (var y = 0; y < Height; y++)
            {
                var from = Buffer + ((long)y * Stride);
                var to = Buffer + ((long)y * stride);
                for (var x = 0; x < Width; x++)
                {
                    var (b, g, r) = (from[x * 4], from[(x * 4) + 1], from[(x * 4) + 2]);
                    to[x * 3] = b;
                    to[(x * 3) + 1] = g;
                    to[(x * 3) + 2] = r;
                }

                new Span<byte>(to + (Width * 3), stride - (Width * 3)).Clear();
            }

            Format = PixelFormats.Bgr24;
            Stride = stride;
            Size = (long)stride * Height;
        }

        public BitmapSource ToBitmapSource() =>
            BitmapSource.Create(Width, Height, Dpi, Dpi, Format, null, (IntPtr)Buffer, (int)Size, Stride);

        public void Dispose()
        {
            NativeMemory.Free(Buffer);
            Buffer = null;
        }

        private static int StrideOf(int width, bool alpha) => (((width * (alpha ? 32 : 24)) + 31) / 32) * 4;

        private static bool AllAlphaOpaque(ReadOnlySpan<byte> bgra)
        {
            for (var i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] != 255)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
