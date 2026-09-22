using Yanwai.Core.Geometry;
using Yanwai.Core.Windows;

namespace Yanwai.Windows.Tests;

public sealed class WeChatWindowSnapshotTests
{
    [Fact]
    public void Capture_bounds_prefer_the_render_surface_and_preserve_negative_desktop_coordinates()
    {
        var snapshot = new WeChatWindowSnapshot(
            Handle: 123,
            ProcessId: 456,
            ProcessName: "Weixin",
            Title: "WeChat",
            ClassName: "Qt51514QWindowIcon",
            TopLevelBounds: new DesktopPixelRect(-1600, 40, 1200, 900),
            ClientBounds: new DesktopPixelRect(-1592, 72, 1184, 860),
            RenderHandle: 789,
            RenderBounds: new DesktopPixelRect(-1240, 112, 832, 700),
            Monitor: new MonitorSnapshot("DISPLAY2", new DesktopPixelRect(-1920, 0, 1920, 1080), new DesktopPixelRect(-1920, 0, 1920, 1040), false),
            Dpi: new DpiSnapshot(144, 144),
            IsVisible: true,
            IsMinimized: false,
            IsForeground: true,
            ObservedAt: DateTimeOffset.UtcNow);

        Assert.Equal(new DesktopPixelRect(-1240, 112, 832, 700), snapshot.CaptureBounds);
        Assert.Equal(1.5, snapshot.Dpi.ScaleX);
    }

    [Fact]
    public void Dpi_transform_round_trips_negative_desktop_pixels_through_monitor_relative_dips()
    {
        var monitor = new MonitorSnapshot(
            "DISPLAY2",
            new DesktopPixelRect(-1920, 0, 1920, 1080),
            new DesktopPixelRect(-1920, 0, 1920, 1040),
            false);
        var transform = new DpiCoordinateTransform(monitor, new DpiSnapshot(144, 144));
        var desktopPixels = new DesktopPixelRect(-1800, 150, 300, 150);

        var dips = transform.DesktopPixelsToMonitorDips(desktopPixels);

        Assert.Equal(new MonitorDipRect(80, 100, 200, 100), dips);
        Assert.Equal(desktopPixels, transform.MonitorDipsToDesktopPixels(dips));
    }
}
