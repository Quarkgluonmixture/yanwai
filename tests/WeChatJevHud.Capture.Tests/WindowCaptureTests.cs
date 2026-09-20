using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Capture.Tests;

public sealed class WindowCaptureTests
{
    [Fact]
    public void Capture_refuses_a_minimized_window_before_reading_the_screen()
    {
        var minimized = new WeChatWindowSnapshot(
            Handle: 123,
            ProcessId: 456,
            ProcessName: "Weixin",
            Title: "WeChat",
            ClassName: "Qt51514QWindowIcon",
            TopLevelBounds: new DesktopPixelRect(100, 100, 800, 600),
            ClientBounds: new DesktopPixelRect(108, 132, 784, 560),
            RenderHandle: null,
            RenderBounds: null,
            Monitor: new MonitorSnapshot("DISPLAY1", new DesktopPixelRect(0, 0, 1920, 1080), new DesktopPixelRect(0, 0, 1920, 1040), true),
            Dpi: new DpiSnapshot(96, 96),
            IsVisible: true,
            IsMinimized: true,
            IsForeground: false,
            ObservedAt: DateTimeOffset.UtcNow);

        var error = Assert.Throws<WindowCaptureUnavailableException>(() =>
            new Win32ScreenRegionCapture().Capture(minimized));

        Assert.Contains("minimized", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Png_writer_creates_a_png_for_a_valid_frame()
    {
        var frame = new CapturedFrame(
            width: 2,
            height: 2,
            stride: 8,
            bgra32Pixels:
            [
                0, 0, 255, 255, 0, 255, 0, 255,
                255, 0, 0, 255, 255, 255, 255, 255,
            ],
            desktopBounds: new DesktopPixelRect(-2, 10, 2, 2),
            method: CaptureMethod.RenderWindow,
            capturedAt: DateTimeOffset.UtcNow,
            duration: TimeSpan.FromMilliseconds(1));
        var directory = Path.Combine(Path.GetTempPath(), $"WeChatJevHud-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "frame.png");

        try
        {
            PngFrameWriter.Save(frame, path);
            var bytes = File.ReadAllBytes(path);

            Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], bytes[..8]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
