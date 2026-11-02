using System.Windows;

namespace ClipboardManager.Popup;

/// <summary>A rectangle in physical screen pixels.</summary>
internal readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>Where the history window goes, in physical screen pixels. Pure arithmetic, so it is unit-tested.</summary>
internal static class PopupPlacement
{
    /// <summary>Space the window keeps from the edges of the work area (and the least gap above it).</summary>
    public const int Margin = 8;

    /// <summary>The window's size on a monitor: logical size × DPI × text size, never larger than the work area.</summary>
    public static (int Width, int Height) PixelSize(Size logical, uint dpi, double textScale, PixelRect work)
    {
        var scale = dpi / 96.0 * textScale;
        var width = Math.Min((int)Math.Round(logical.Width * scale), work.Width - (2 * Margin));
        var height = Math.Min((int)Math.Round(logical.Height * scale), work.Height - (2 * Margin));
        return (Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>The default spot: centred horizontally, in the upper third of the work area.</summary>
    public static PixelRect Default(PixelRect work, uint dpi, Size logical, double textScale)
    {
        var (width, height) = PixelSize(logical, dpi, textScale, work);
        var x = work.Left + ((work.Width - width) / 2);
        var y = work.Top + Math.Max(Margin, (work.Height - height) / 3);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// The spot the user dragged the window to (its top-left), pulled inside the work area so the
    /// whole window is visible. Min before Max, not Math.Clamp: that throws when the window is wider
    /// than the work area, and here the top-left edge wins.
    /// </summary>
    public static PixelRect Remembered(int left, int top, PixelRect work, uint dpi, Size logical, double textScale)
    {
        var (width, height) = PixelSize(logical, dpi, textScale, work);
        var x = Math.Max(work.Left, Math.Min(left, work.Right - width));
        var y = Math.Max(work.Top, Math.Min(top, work.Bottom - height));
        return new PixelRect(x, y, width, height);
    }

    /// <summary>The saved spot at 96 DPI: finds the monitor it is on before that monitor's DPI is known.</summary>
    public static PixelRect Probe(int left, int top, Size logical, double textScale) =>
        new(left, top, Math.Max(1, (int)Math.Round(logical.Width * textScale)), Math.Max(1, (int)Math.Round(logical.Height * textScale)));
}
