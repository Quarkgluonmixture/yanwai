using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Overlay;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public sealed class OverlayPlacementTests
{
    private static readonly DesktopPixelRect WorkArea = new(0, 0, 2560, 1600);
    private static readonly (int Width, int Height) Size = (480, 320);

    [Fact]
    public void CaptureCoordinatesAreOffsetByTheFrameOrigin()
    {
        var frame = new DesktopPixelRect(1920, -180, 2560, 1600);

        var desktop = OverlayPlacement.CaptureToDesktop(new CapturePixelRect(639, 292, 441, 233), frame);

        Assert.Equal(new DesktopPixelRect(2559, 112, 441, 233), desktop);
    }

    [Fact]
    public void ANegativeFrameOriginSurvivesTheOffset()
    {
        // A secondary monitor left of the primary has negative desktop coordinates;
        // treating them as unsigned would throw the HUD across the desktop.
        var frame = new DesktopPixelRect(-1920, 0, 1920, 1080);

        var desktop = OverlayPlacement.CaptureToDesktop(new CapturePixelRect(100, 50, 200, 60), frame);

        Assert.Equal(-1820, desktop.X);
        Assert.Equal(50, desktop.Y);
    }

    [Fact]
    public void TheNormalPositionIsToTheRightOfTheMessage()
    {
        var anchor = new DesktopPixelRect(639, 292, 441, 233);

        var result = OverlayPlacement.Place(anchor, Size, WorkArea, 12);

        Assert.Equal(OverlaySide.Right, result.Side);
        Assert.Equal(639 + 441 + 12, result.Bounds.X);
        Assert.Equal(292, result.Bounds.Y);
    }

    [Fact]
    public void ItFlipsLeftWhenTheRightEdgeIsTooClose()
    {
        var anchor = new DesktopPixelRect(2000, 300, 400, 100);

        var result = OverlayPlacement.Place(anchor, Size, WorkArea, 12);

        Assert.Equal(OverlaySide.Left, result.Side);
        Assert.Equal(2000 - 12 - 480, result.Bounds.X);
    }

    [Fact]
    public void ItDropsBelowOnlyWhenNeitherSideFits()
    {
        var narrow = new DesktopPixelRect(0, 0, 700, 1600);
        var anchor = new DesktopPixelRect(100, 300, 400, 100);

        var result = OverlayPlacement.Place(anchor, Size, narrow, 12);

        Assert.Equal(OverlaySide.Below, result.Side);
        Assert.Equal(300 + 100 + 12, result.Bounds.Y);
    }

    [Fact]
    public void ThePlacedHudNeverOverlapsTheMessage()
    {
        // The one rule that cannot bend: covering the message defeats the HUD.
        var anchors = new[]
        {
            new DesktopPixelRect(639, 292, 441, 233),
            new DesktopPixelRect(2100, 1500, 400, 90),
            new DesktopPixelRect(0, 0, 300, 60),
            new DesktopPixelRect(2400, 10, 150, 60),
        };

        foreach (var anchor in anchors)
        {
            var result = OverlayPlacement.Place(anchor, Size, WorkArea, 12);
            Assert.False(Intersects(result.Bounds, anchor), $"overlapped for anchor {anchor}");
        }
    }

    [Fact]
    public void TheHudStaysInsideTheWorkArea()
    {
        var anchor = new DesktopPixelRect(600, 1560, 300, 40);

        var result = OverlayPlacement.Place(anchor, Size, WorkArea, 12);

        Assert.True(result.Bounds.Y >= WorkArea.Y);
        Assert.True(result.Bounds.Y + result.Bounds.Height <= WorkArea.Y + WorkArea.Height);
    }

    [Fact]
    public void ItPlacesAgainstTheMonitorTheWindowIsOn()
    {
        // Work area of a monitor left of the primary. The HUD must land on that
        // monitor, not be clamped to the primary's origin.
        var left = new DesktopPixelRect(-1920, 0, 1920, 1080);
        var anchor = new DesktopPixelRect(-1800, 400, 300, 80);

        var result = OverlayPlacement.Place(anchor, Size, left, 12);

        Assert.Equal(OverlaySide.Right, result.Side);
        Assert.Equal(-1800 + 300 + 12, result.Bounds.X);
        Assert.True(result.Bounds.X + result.Bounds.Width <= left.X + left.Width);
    }

    [Fact]
    public void AnAnchorScrolledOutOfTheChatAreaIsNotVisible()
    {
        var chat = new CapturePixelRect(525, 139, 2035, 1189);

        Assert.False(OverlayPlacement.IsAnchorVisible(new CapturePixelRect(639, 1400, 200, 60), chat));
        Assert.False(OverlayPlacement.IsAnchorVisible(new CapturePixelRect(639, 40, 200, 60), chat));
    }

    [Fact]
    public void AnAnchorMostlyInsideTheChatAreaStaysVisible()
    {
        var chat = new CapturePixelRect(525, 139, 2035, 1189);

        Assert.True(OverlayPlacement.IsAnchorVisible(new CapturePixelRect(639, 292, 441, 233), chat));

        // Half of a bubble past the top edge still counts; a sliver does not.
        Assert.True(OverlayPlacement.IsAnchorVisible(new CapturePixelRect(639, 109, 200, 60), chat));
        Assert.False(OverlayPlacement.IsAnchorVisible(new CapturePixelRect(639, 89, 200, 60), chat));
    }

    [Fact]
    public void AnEmptyAnchorIsNeverVisible()
    {
        var chat = new CapturePixelRect(525, 139, 2035, 1189);

        Assert.False(OverlayPlacement.IsAnchorVisible(new CapturePixelRect(639, 292, 0, 0), chat));
    }

    private static bool Intersects(DesktopPixelRect a, DesktopPixelRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width &&
        a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
