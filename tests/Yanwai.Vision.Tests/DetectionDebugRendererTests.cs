using Yanwai.Capture;

namespace Yanwai.Vision.Tests;

public sealed class DetectionDebugRendererTests
{
    [Fact]
    public void Render_preserves_frame_geometry_and_draws_the_chat_region()
    {
        var frame = PngFrameReader.Load(FixturePath("wechat-dark-layout-reference.png"));
        var result = new BubbleDetectionPipeline(
            new DarkThemeChatRegionLocator(),
            new DarkThemeBubbleDetector()).Analyze(frame);

        var debugFrame = DetectionDebugRenderer.Render(frame, result);

        Assert.Equal(frame.Width, debugFrame.Width);
        Assert.Equal(frame.Height, debugFrame.Height);
        Assert.NotEqual(
            Pixel(frame, result.ChatRegion.Bounds.X, result.ChatRegion.Bounds.Y),
            Pixel(debugFrame, result.ChatRegion.Bounds.X, result.ChatRegion.Bounds.Y));
    }

    private static (byte Blue, byte Green, byte Red) Pixel(CapturedFrame frame, int x, int y)
    {
        var offset = (y * frame.Stride) + (x * 4);
        return (frame.Bgra32Pixels[offset], frame.Bgra32Pixels[offset + 1], frame.Bgra32Pixels[offset + 2]);
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
