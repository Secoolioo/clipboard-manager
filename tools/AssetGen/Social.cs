using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetGen;

/// <summary>
/// The 1280x640 social preview card GitHub shows for links to the repository (uploaded once in the
/// repository settings). Built from the logo and the dark README screenshot.
/// </summary>
internal static class Social
{
    private const int Width = 1280;
    private const int Height = 640;

    public static void Write(string root)
    {
        var logo = Load(Path.Combine(root, "assets", "brand", "logo-512.png"));
        var shot = Load(Path.Combine(root, "assets", "readme", "popup-dark.png"));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Hex("#0B1512")), null, new Rect(0, 0, Width, Height));
            var glow = new RadialGradientBrush(Color.FromArgb(70, 0x4D, 0xE3, 0xA0), Colors.Transparent)
            {
                Center = new Point(0.78, 0.45),
                GradientOrigin = new Point(0.78, 0.45),
                RadiusX = 0.55,
                RadiusY = 0.8,
            };
            dc.DrawRectangle(glow, null, new Rect(0, 0, Width, Height));

            // Screenshot on the right, slightly cut off at the edge like a peek into the app.
            var shotWidth = 700.0;
            var shotHeight = shotWidth * shot.PixelHeight / shot.PixelWidth;
            dc.DrawImage(shot, new Rect(Width - shotWidth + 40, (Height - shotHeight) / 2, shotWidth, shotHeight));

            const double left = 76;
            dc.DrawImage(logo, new Rect(left, 92, 96, 96));
            var y = 214.0;
            y = Text(dc, "Clipboard Manager", left, y, 58, "#FFFFFF", FontWeights.Bold);
            y = Text(dc, "for Windows 10 & 11", left, y + 2, 30, "#4DE3A0", FontWeights.SemiBold);
            y += 26;
            foreach (var line in new[] { "Ctrl+Shift+V · instant search · pins", "Local only · no cloud · no telemetry", "One portable EXE · free & open source" })
            {
                dc.DrawEllipse(new SolidColorBrush(Hex("#4DE3A0")), null, new Point(left + 6, y + 15), 4, 4);
                y = Text(dc, line, left + 24, y, 23, "#C9D8D1", FontWeights.Normal) + 8;
            }

            Text(dc, "github.com/Secoolioo/clipboard-manager", left, Height - 74, 20, "#7F9A8E", FontWeights.Normal);
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var file = Path.Combine(root, "assets", "brand", "social-preview.png");
        using (var stream = File.Create(file))
        {
            encoder.Save(stream);
        }

        Console.WriteLine($"social: assets/brand/social-preview.png ({new FileInfo(file).Length / 1024} KB)");
    }

    private static double Text(DrawingContext dc, string text, double x, double y, double size, string color, FontWeight weight)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(Hex(color)), 1.0)
        {
            MaxTextWidth = 560,
        };
        dc.DrawText(formatted, new Point(x, y));
        return y + formatted.Height;
    }

    private static BitmapImage Load(string file)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(file);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
