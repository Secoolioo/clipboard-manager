using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardManager.Shell;

/// <summary>
/// The app icon as vector geometry (256×256 design grid). One source for the EXE icon, the tray
/// icon (rendered at the exact DPI size, light/dark taskbar, paused state) and the README artwork.
/// </summary>
internal static class BrandIcon
{
    public static readonly Color Green = Color.FromRgb(0x16, 0xD6, 0x7A);
    public static readonly Color Teal = Color.FromRgb(0x0E, 0xA5, 0xA4);
    public static readonly Color Ink = Color.FromRgb(0x06, 0x1A, 0x14);

    private static readonly Rect Board = new(30, 42, 196, 198);
    private static readonly Rect Clip = new(86, 16, 84, 46);

    private static readonly Rect[] Bars =
    [
        new(62, 102, 132, 24),
        new(62, 146, 104, 24),
        new(62, 190, 72, 24),
    ];

    /// <summary>Full-colour icon (EXE, README).</summary>
    public static Drawing CreateColor()
    {
        var group = new DrawingGroup();
        var gradient = new LinearGradientBrush(Green, Teal, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        group.Children.Add(new GeometryDrawing(gradient, null, new RectangleGeometry(Board, 40, 40)));
        group.Children.Add(new GeometryDrawing(Frozen(Ink), null, new RectangleGeometry(Clip, 16, 16)));
        group.Children.Add(new GeometryDrawing(Frozen(Green), null, new RectangleGeometry(new Rect(104, 28, 48, 12), 6, 6)));

        double[] opacities = [1.0, 0.82, 0.62];
        for (var i = 0; i < Bars.Length; i++)
        {
            group.Children.Add(new GeometryDrawing(Frozen(Color.FromArgb((byte)(255 * opacities[i]), 255, 255, 255)), null, new RectangleGeometry(Bars[i], 12, 12)));
        }

        group.Freeze();
        return group;
    }

    /// <summary>Single-colour silhouette for the notification area; bars are cut out so it reads at 16 px.</summary>
    public static Drawing CreateGlyph(Color color, bool paused)
    {
        var brush = Frozen(color);
        Geometry board = new RectangleGeometry(Board, 36, 36);
        var cutouts = new GeometryGroup();
        cutouts.Children.Add(new RectangleGeometry(Inflate(Clip, 14), 24, 24));
        foreach (var bar in Bars)
        {
            cutouts.Children.Add(new RectangleGeometry(Inflate(bar, 3), 14, 14));
        }

        var badge = new EllipseGeometry(new Point(200, 206), 62, 62);
        if (paused)
        {
            cutouts.Children.Add(badge);
        }

        board = new CombinedGeometry(GeometryCombineMode.Exclude, board, cutouts);

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(brush, null, board));
        group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(Clip, 16, 16)));
        if (paused)
        {
            group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(170, 174, 20, 64), 6, 6)));
            group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(210, 174, 20, 64), 6, 6)));
        }

        group.Freeze();
        return group;
    }

    /// <summary>Renders a drawing into a square PNG of <paramref name="pixels"/> size.</summary>
    public static byte[] RenderPng(Drawing drawing, int pixels)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var scale = pixels / 256.0;
            context.PushTransform(new ScaleTransform(scale, scale));
            context.DrawDrawing(drawing);
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Rect Inflate(Rect rect, double by)
    {
        rect.Inflate(by, by);
        return rect;
    }
}
