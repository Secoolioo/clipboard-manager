using System.Windows;
using ClipboardManager.Popup;

namespace ClipboardManager.Tests.Unit;

/// <summary>Where the history window opens (issue #12: movable, the spot remembered), in physical pixels.</summary>
[Trait("Category", "Unit")]
public sealed class PopupPlacementTests
{
    private static readonly Size Full = new(PopupWindow.FullWidth, PopupWindow.BaseHeight);
    private static readonly Size Compact = new(PopupWindow.CompactWidth, PopupWindow.BaseHeight);

    // 1920 × 1080 with the taskbar at the bottom.
    private static readonly PixelRect Work = new(0, 0, 1920, 1032);

    [Fact]
    public void The_default_spot_is_centred_in_the_upper_third_as_before_0_12()
    {
        Assert.Equal(new PixelRect(570, 184, 780, 480), PopupPlacement.Default(Work, 96, Full, 1));

        // Taskbar at the top: the work area starts lower.
        Assert.Equal(new PixelRect(570, 232, 780, 480), PopupPlacement.Default(new PixelRect(0, 48, 1920, 1032), 96, Full, 1));
    }

    [Fact]
    public void The_size_follows_the_monitor_dpi_and_the_text_size()
    {
        Assert.Equal(new PixelRect(695, 226, 1170, 720), PopupPlacement.Default(new PixelRect(0, 0, 2560, 1400), 144, Full, 1));
        Assert.Equal((975, 600), PopupPlacement.PixelSize(Full, 96, 1.25, Work));
    }

    [Fact]
    public void A_remembered_spot_inside_the_work_area_is_kept()
    {
        Assert.Equal(new PixelRect(300, 100, 780, 480), PopupPlacement.Remembered(300, 100, Work, 96, Full, 1));
    }

    [Theory]
    [InlineData(1800, 900, 1140, 552)]
    [InlineData(-50, -20, 0, 0)]
    [InlineData(5000, -5000, 1140, 0)]
    public void A_remembered_spot_is_pulled_inside_the_work_area(int left, int top, int x, int y)
    {
        var place = PopupPlacement.Remembered(left, top, Work, 96, Full, 1);

        Assert.Equal(new PixelRect(x, y, 780, 480), place);
        Assert.True(place.Left >= Work.Left && place.Right <= Work.Right && place.Top >= Work.Top && place.Bottom <= Work.Bottom);
    }

    [Fact]
    public void A_monitor_left_of_and_above_the_primary_one_has_negative_coordinates()
    {
        var left = new PixelRect(-2560, -200, 2560, 1400);

        Assert.Equal(new PixelRect(-2000, -150, 780, 480), PopupPlacement.Remembered(-2000, -150, left, 96, Full, 1));
        Assert.Equal(new PixelRect(-1670, 106, 780, 480), PopupPlacement.Default(left, 96, Full, 1));
    }

    [Fact]
    public void Switching_to_compact_keeps_the_left_edge()
    {
        var full = PopupPlacement.Remembered(300, 100, Work, 96, Full, 1);
        var compact = PopupPlacement.Remembered(300, 100, Work, 96, Compact, 1);

        Assert.Equal(full.Left, compact.Left);
        Assert.Equal(full.Top, compact.Top);
        Assert.Equal(420, compact.Width);
    }

    [Fact]
    public void A_work_area_smaller_than_the_window_does_not_throw()
    {
        var tiny = new PixelRect(0, 0, 10, 10);

        var place = PopupPlacement.Remembered(500, 500, tiny, 96, Full, 1);
        var fallback = PopupPlacement.Default(tiny, 96, Full, 1);

        Assert.True(place.Width >= 1 && place.Height >= 1);
        Assert.True(place.Left >= tiny.Left && place.Top >= tiny.Top);
        Assert.True(fallback.Width >= 1 && fallback.Height >= 1);
    }

    [Fact]
    public void The_probe_uses_96_dpi_and_the_text_size()
    {
        Assert.Equal(new PixelRect(100, 200, 1170, 720), PopupPlacement.Probe(100, 200, Full, 1.5));
    }
}
