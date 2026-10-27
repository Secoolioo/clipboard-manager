using System.Buffers.Binary;
using ClipboardManager.Clipboard;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;

namespace ClipboardManager.Tests.Unit;

/// <summary>
/// The clipboard image codec on hand-made bitmaps and PNGs. Runs on a plain (MTA) test thread on
/// purpose: the clipboard reader decodes on such a thread too.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ImageCodecTests
{
    private static readonly (byte B, byte G, byte R, byte A) Red = (0, 0, 255, 255);
    private static readonly (byte B, byte G, byte R, byte A) Blue = (255, 0, 0, 255);

    private static (byte B, byte G, byte R, byte A) PixelAt((int Width, int Height, byte[] Bgra) image, int x, int y)
    {
        var i = ((y * image.Width) + x) * 4;
        return (image.Bgra[i], image.Bgra[i + 1], image.Bgra[i + 2], image.Bgra[i + 3]);
    }

    private static CapturedImage Normalized(ClipboardImageFormat format, byte[] data)
    {
        var (reason, image) = TestImages.Normalize(format, data);
        Assert.Equal(SkipReason.None, reason);
        return image!;
    }

    [Fact]
    public void A_24_bit_bottom_up_bitmap_with_row_padding_keeps_its_orientation()
    {
        // 3 pixels of 3 bytes: every row has 3 bytes of padding.
        var image = Normalized(ClipboardImageFormat.Dib, TestImages.Dib(3, 2, 24, (_, y) => y == 0 ? Red : Blue));

        var pixels = TestImages.Pixels(image.Png);
        Assert.Equal((3, 2), (image.Width, image.Height));
        Assert.Equal(Red, PixelAt(pixels, 2, 0));
        Assert.Equal(Blue, PixelAt(pixels, 0, 1));
        Assert.Equal(2, TestImages.ColorType(image.Png));
    }

    [Fact]
    public void A_32_bit_top_down_bitmap_keeps_its_orientation()
    {
        var image = Normalized(ClipboardImageFormat.Dib, TestImages.Dib(2, 3, 32, (_, y) => y == 0 ? Red : Blue, topDown: true));

        var pixels = TestImages.Pixels(image.Png);
        Assert.Equal(Red, PixelAt(pixels, 1, 0));
        Assert.Equal(Blue, PixelAt(pixels, 1, 2));
    }

    [Fact]
    public void A_32_bit_bitmap_whose_alpha_is_all_zero_is_opaque()
    {
        var image = Normalized(ClipboardImageFormat.Dib, TestImages.Dib(2, 2, 32, (_, _) => (0x30, 0x60, 0x90, 0)));

        Assert.Equal(2, TestImages.ColorType(image.Png));
        Assert.Equal(((byte)0x30, (byte)0x60, (byte)0x90, (byte)255), PixelAt(TestImages.Pixels(image.Png), 1, 1));
    }

    [Fact]
    public void Transparency_of_a_32_bit_bitmap_is_kept()
    {
        var image = Normalized(ClipboardImageFormat.Dib, TestImages.Dib(2, 1, 32, (x, _) => x == 0 ? ((byte)10, (byte)20, (byte)30, (byte)0) : Red));

        var pixels = TestImages.Pixels(image.Png);
        Assert.Equal(6, TestImages.ColorType(image.Png));
        Assert.Equal(0, PixelAt(pixels, 0, 0).A);
        Assert.Equal(Red, PixelAt(pixels, 1, 0));
    }

    [Fact]
    public void Masks_after_the_header_are_skipped_and_their_undefined_alpha_byte_is_ignored()
    {
        // What Windows synthesizes from a 32-bit CF_BITMAP: BITMAPINFOHEADER, BI_BITFIELDS, three masks.
        var masks = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(masks, 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(4), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(8), 0x000000FF);
        var dib = TestImages.Dib(3, 1, 32, (x, _) => x == 1 ? ((byte)0, (byte)255, (byte)0, (byte)7) : Red, compression: TestImages.BiBitFields, extra: masks);

        var image = Normalized(ClipboardImageFormat.Dib, dib);

        var pixels = TestImages.Pixels(image.Png);
        Assert.Equal(2, TestImages.ColorType(image.Png));
        Assert.Equal(Red, PixelAt(pixels, 0, 0));
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), PixelAt(pixels, 1, 0));
        Assert.Equal(Red, PixelAt(pixels, 2, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_v5_header_with_an_alpha_mask_keeps_transparency(bool masksRepeatedAfterHeader)
    {
        var repeated = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(repeated, 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(repeated.AsSpan(4), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(repeated.AsSpan(8), 0x000000FF);
        var dib = TestImages.Dib(2, 1, 32, (x, _) => x == 0 ? ((byte)0, (byte)0, (byte)255, (byte)64) : Blue,
            headerSize: 124, compression: TestImages.BiBitFields, extra: masksRepeatedAfterHeader ? repeated : null);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52), 0xFF000000);

        var image = Normalized(ClipboardImageFormat.Dib, dib);

        var pixels = TestImages.Pixels(image.Png);
        Assert.Equal(6, TestImages.ColorType(image.Png));
        Assert.Equal(64, PixelAt(pixels, 0, 0).A);
        Assert.Equal(Blue, PixelAt(pixels, 1, 0));
    }

    [Fact]
    public void A_palette_bitmap_goes_through_the_bmp_decoder()
    {
        // Two palette entries (BGR0): 0 = red, 1 = blue.
        byte[] palette = [0, 0, 255, 0, 255, 0, 0, 0];
        var dib = TestImages.Dib(2, 2, 8, (x, y) => ((byte)((x + y) % 2), 0, 0, 0), extra: palette, colorsUsed: 2);

        var image = Normalized(ClipboardImageFormat.Dib, dib);

        var pixels = TestImages.Pixels(image.Png);
        Assert.Equal(Red, PixelAt(pixels, 0, 0));
        Assert.Equal(Blue, PixelAt(pixels, 1, 0));
        Assert.Equal(Blue, PixelAt(pixels, 0, 1));
    }

    [Fact]
    public void Broken_bitmaps_are_not_stored()
    {
        var valid = TestImages.Dib(4, 4, 32, (_, _) => Red);
        byte[][] broken =
        [
            [],
            [1, 2, 3, 4, 5],
            valid[..^1],
            Patched(valid, 0, 12),       // OS/2 core header
            Patched(valid, 4, 0),        // zero width
            Patched(valid, 14, 7),       // 7 bits per pixel
            Patched(valid, 16, 4),       // BI_JPEG
            Patched(valid, 32, 1000),    // more than 256 palette entries
        ];

        foreach (var data in broken)
        {
            Assert.Equal(SkipReason.NotText, TestImages.Normalize(ClipboardImageFormat.Dib, data).Reason);
        }
    }

    [Fact]
    public void A_huge_size_in_the_header_is_too_large_before_anything_is_read()
    {
        var header = TestImages.Dib(1, 1, 32, (_, _) => Red);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 20_000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 20_000);

        Assert.Equal(SkipReason.TooLarge, ImageCodec.CheckHeader(ClipboardImageFormat.Dib, header));
        Assert.Equal(SkipReason.TooLarge, TestImages.Normalize(ClipboardImageFormat.Dib, header).Reason);
    }

    [Fact]
    public void Png_headers_are_checked_before_copying()
    {
        var png = TestImages.Png(4, 3, (_, _) => Red);
        Assert.Equal(SkipReason.None, ImageCodec.CheckHeader(ClipboardImageFormat.Png, png));

        var huge = png.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16), 100_000);
        Assert.Equal(SkipReason.TooLarge, ImageCodec.CheckHeader(ClipboardImageFormat.Png, huge));

        Assert.Equal(SkipReason.NotText, ImageCodec.CheckHeader(ClipboardImageFormat.Png, [.. png.AsSpan(1)]));
        Assert.Equal(SkipReason.NotText, TestImages.Normalize(ClipboardImageFormat.Png, [.. png.AsSpan(0, 40)]).Reason);
    }

    [Fact]
    public void An_opaque_png_is_stored_without_an_alpha_channel()
    {
        var image = Normalized(ClipboardImageFormat.Png, TestImages.Png(5, 5, (x, _) => x % 2 == 0 ? Red : Blue));

        Assert.Equal(2, TestImages.ColorType(image.Png));
        Assert.Equal(Blue, PixelAt(TestImages.Pixels(image.Png), 1, 4));
    }

    [Fact]
    public void The_same_picture_as_png_and_as_bitmap_gives_the_same_entry()
    {
        static (byte B, byte G, byte R, byte A) Pattern(int x, int y) => ((byte)(x * 20), (byte)(y * 30), (byte)((x * y) % 256), 255);

        // A browser's PNG (at its screen DPI) and a screenshot tool's bitmap of the same pixels.
        var fromPng = Normalized(ClipboardImageFormat.Png, TestImages.Png(9, 7, Pattern, dpi: 144));
        var fromDib = Normalized(ClipboardImageFormat.Dib, TestImages.Dib(9, 7, 32, Pattern));
        var fromDib24 = Normalized(ClipboardImageFormat.Dib, TestImages.Dib(9, 7, 24, Pattern));

        Assert.Equal(fromPng.Png, fromDib.Png);
        Assert.Equal(fromPng.Png, fromDib24.Png);
        Assert.Equal(ContentHash.ComputeImage(fromPng.Png), ContentHash.ComputeImage(fromDib.Png));
    }

    [Fact]
    public void Normalizing_a_stored_picture_again_gives_the_same_bytes()
    {
        foreach (var image in new[] { TestImages.Captured(40, 30), TestImages.Captured(40, 30, transparent: true) })
        {
            Assert.Equal(image.Png, Normalized(ClipboardImageFormat.Png, image.Png).Png);
        }
    }

    [Fact]
    public void Metadata_of_the_source_is_not_kept()
    {
        var canary = Canary.Create();
        var png = TestImages.WithTextChunk(TestImages.Png(6, 6, (_, _) => Red), "Comment", canary);
        Assert.True(png.AsSpan().IndexOf(System.Text.Encoding.Latin1.GetBytes(canary)) >= 0);

        var image = Normalized(ClipboardImageFormat.Png, png);

        Assert.True(image.Png.AsSpan().IndexOf(System.Text.Encoding.Latin1.GetBytes(canary)) < 0);
        Assert.Equal(Red, PixelAt(TestImages.Pixels(image.Png), 5, 5));
    }

    [Theory]
    [InlineData(400, 100, 96, 24)]
    [InlineData(100, 400, 14, 56)]
    [InlineData(50, 20, 50, 20)]
    [InlineData(4000, 1, 96, 1)]
    public void Thumbnails_fit_the_box_keep_the_aspect_ratio_and_never_grow(int width, int height, int thumbnailWidth, int thumbnailHeight)
    {
        var image = Normalized(ClipboardImageFormat.Dib, TestImages.Dib(width, height, 24, (x, y) => ((byte)x, (byte)y, 0, 255)));

        var thumbnail = TestImages.Pixels(image.Thumbnail);
        Assert.Equal((thumbnailWidth, thumbnailHeight), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public void The_bitmap_for_other_apps_is_24_bit_bottom_up_on_white()
    {
        var image = Normalized(ClipboardImageFormat.Dib, TestImages.Dib(2, 2, 32, (x, y) => (x, y) switch
        {
            (0, 0) => ((byte)0, (byte)0, (byte)255, (byte)128),
            (1, 0) => ((byte)0, (byte)0, (byte)0, (byte)0),
            _ => Blue,
        }));

        var dib = ImageCodec.TryCreateDib(image.Png)!;

        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(dib));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16)));
        Assert.Equal(40 + (8 * 2), dib.Length);

        // Bottom-up: the top row is the second one in the data. Half-transparent red on white is
        // light red, fully transparent is white, and the row padding stays zero.
        var top = dib.AsSpan(40 + 8, 8);
        Assert.Equal([127, 127, 255, 255, 255, 255, 0, 0], top.ToArray());
        Assert.Equal([255, 0, 0, 255, 0, 0, 0, 0], dib.AsSpan(40, 8).ToArray());
    }

    [Fact]
    public void Garbage_is_not_turned_into_a_bitmap()
    {
        Assert.Null(ImageCodec.TryCreateDib([1, 2, 3, 4]));
        Assert.Null(ImageCodec.DecodeForDisplay([1, 2, 3, 4], 1, 1));
    }

    [Fact]
    public void Pictures_for_display_are_frozen_and_scaled_down_to_the_longer_side()
    {
        var image = TestImages.Captured(400, 100);

        var full = ImageCodec.DecodeForDisplay(image.Png, image.Width, image.Height)!;
        var scaled = ImageCodec.DecodeForDisplay(image.Png, image.Width, image.Height, maxSide: 200)!;

        Assert.True(full.IsFrozen);
        Assert.Equal((400, 100), (full.PixelWidth, full.PixelHeight));
        Assert.Equal((200, 50), (scaled.PixelWidth, scaled.PixelHeight));
    }

    private static byte[] Patched(byte[] dib, int offset, int value)
    {
        var copy = dib.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }
}
