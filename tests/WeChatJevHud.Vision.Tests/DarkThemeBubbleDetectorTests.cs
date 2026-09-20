using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;

namespace WeChatJevHud.Vision.Tests;

public sealed class DarkThemeBubbleDetectorTests
{
    [Fact]
    public void Detect_returns_only_remote_and_self_text_bubbles_from_the_reference_fixture()
    {
        var frame = PngFrameReader.Load(FixturePath("wechat-dark-layout-reference.png"));
        var chatRegion = new DarkThemeChatRegionLocator().Locate(frame).Bounds;

        var bubbles = new DarkThemeBubbleDetector().Detect(frame, chatRegion);

        (MessageSide Side, CapturePixelRect Bounds)[] expected =
        [
            (MessageSide.Self, new CapturePixelRect(1067, 120, 106, 39)),
            (MessageSide.Self, new CapturePixelRect(1004, 189, 169, 54)),
            (MessageSide.Self, new CapturePixelRect(836, 273, 337, 54)),
            (MessageSide.Remote, new CapturePixelRect(478, 357, 154, 54)),
            (MessageSide.Self, new CapturePixelRect(702, 503, 471, 54)),
            (MessageSide.Self, new CapturePixelRect(920, 648, 253, 54)),
            (MessageSide.Self, new CapturePixelRect(618, 794, 555, 54)),
            (MessageSide.Remote, new CapturePixelRect(478, 939, 127, 54)),
            (MessageSide.Remote, new CapturePixelRect(478, 1085, 169, 54)),
            (MessageSide.Remote, new CapturePixelRect(478, 1169, 127, 54)),
            (MessageSide.Remote, new CapturePixelRect(478, 1314, 244, 54)),
        ];

        Assert.Equal(expected.Length, bubbles.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Side, bubbles[index].Side);
            Assert.Equal(expected[index].Bounds, bubbles[index].Bounds);
        }

        Assert.All(bubbles, bubble =>
        {
            Assert.InRange(bubble.Confidence, 0.75, 1);
            Assert.True(bubble.Bounds.X >= chatRegion.X);
            Assert.True(bubble.Bounds.Y >= chatRegion.Y);
            Assert.True(bubble.Bounds.Right <= chatRegion.Right);
            Assert.True(bubble.Bounds.Bottom <= chatRegion.Bottom);
        });
    }

    [Fact]
    public void Detect_returns_unknown_for_an_unanchored_text_bubble_and_ignores_a_solid_color_block()
    {
        var frame = SyntheticFrame(width: 320, height: 180);
        PaintRectangle(frame, x: 120, y: 30, width: 80, height: 40, red: 84, green: 210, blue: 139);
        PaintRectangle(frame, x: 145, y: 44, width: 12, height: 10, red: 30, green: 30, blue: 30);
        PaintRectangle(frame, x: 230, y: 105, width: 70, height: 35, red: 84, green: 210, blue: 139);

        var bubbles = new DarkThemeBubbleDetector().Detect(
            frame,
            new CapturePixelRect(0, 0, frame.Width, frame.Height));

        var bubble = Assert.Single(bubbles);
        Assert.Equal(MessageSide.Unknown, bubble.Side);
        Assert.Equal(120, bubble.Bounds.X);
        Assert.Equal(30, bubble.Bounds.Y);
    }

    private static CapturedFrame SyntheticFrame(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 31;
            pixels[offset + 1] = 30;
            pixels[offset + 2] = 30;
            pixels[offset + 3] = 255;
        }

        return new CapturedFrame(
            width,
            height,
            stride,
            pixels,
            new DesktopPixelRect(0, 0, width, height),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
    }

    private static void PaintRectangle(
        CapturedFrame frame,
        int x,
        int y,
        int width,
        int height,
        byte red,
        byte green,
        byte blue)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                var offset = (row * frame.Stride) + (column * 4);
                frame.Bgra32Pixels[offset] = blue;
                frame.Bgra32Pixels[offset + 1] = green;
                frame.Bgra32Pixels[offset + 2] = red;
                frame.Bgra32Pixels[offset + 3] = 255;
            }
        }
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
