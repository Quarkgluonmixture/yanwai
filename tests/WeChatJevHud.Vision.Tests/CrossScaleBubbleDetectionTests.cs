using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;

namespace WeChatJevHud.Vision.Tests;

public sealed class CrossScaleBubbleDetectionTests
{
    [Theory]
    [InlineData(989, 316, 123, 99, 36, 833, 331, 85, 36)]
    [InlineData(662, 316, 289, 85, 36, 422, 173, 169, 55)]
    public void Analyze_uses_frame_relative_geometry_at_100_percent_scale(
        int frameWidth,
        int remoteX,
        int remoteY,
        int remoteWidth,
        int remoteHeight,
        int selfX,
        int selfY,
        int selfWidth,
        int selfHeight)
    {
        const int frameHeight = 680;
        const int paneLeft = 251;
        const int headerDivider = 79;
        const int roiTop = headerDivider + 1;
        const int composerTop = 589;
        var frame = SyntheticWeChatFrame(frameWidth, frameHeight, paneLeft, headerDivider, composerTop);
        var remoteBounds = new CapturePixelRect(remoteX, remoteY, remoteWidth, remoteHeight);
        var selfBounds = new CapturePixelRect(selfX, selfY, selfWidth, selfHeight);
        PaintTextBubble(frame, remoteBounds, MessageSide.Remote);
        PaintTextBubble(frame, selfBounds, MessageSide.Self);

        var result = new BubbleDetectionPipeline(
            new DarkThemeChatRegionLocator(),
            new DarkThemeBubbleDetector()).Analyze(frame);

        Assert.Equal(
            new CapturePixelRect(paneLeft, roiTop, frameWidth - paneLeft, composerTop - roiTop),
            result.ChatRegion.Bounds);
        Assert.Equal(2, result.Bubbles.Count);
        Assert.Contains(result.Bubbles, bubble => bubble.Side == MessageSide.Remote && bubble.Bounds == remoteBounds);
        Assert.Contains(result.Bubbles, bubble => bubble.Side == MessageSide.Self && bubble.Bounds == selfBounds);
        Assert.All(result.Bubbles, bubble => Assert.InRange(bubble.DetectionScore, 0, 1));
    }

    private static CapturedFrame SyntheticWeChatFrame(
        int width,
        int height,
        int paneLeft,
        int headerDivider,
        int composerTop)
    {
        var frame = SolidFrame(width, height, red: 45, green: 45, blue: 45);
        PaintRectangle(frame, new CapturePixelRect(paneLeft, 0, width - paneLeft, headerDivider), 38, 38, 38);
        PaintRectangle(
            frame,
            new CapturePixelRect(paneLeft, headerDivider, width - paneLeft, composerTop - headerDivider),
            30,
            30,
            30);
        PaintRectangle(
            frame,
            new CapturePixelRect(paneLeft, composerTop, width - paneLeft, height - composerTop),
            40,
            40,
            40);
        return frame;
    }

    private static CapturedFrame SolidFrame(int width, int height, byte red, byte green, byte blue)
    {
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var frame = new CapturedFrame(
            width,
            height,
            stride,
            pixels,
            new DesktopPixelRect(0, 0, width, height),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
        PaintRectangle(frame, new CapturePixelRect(0, 0, width, height), red, green, blue);
        return frame;
    }

    private static void PaintTextBubble(CapturedFrame frame, CapturePixelRect bounds, MessageSide side)
    {
        if (side == MessageSide.Remote)
        {
            PaintRectangle(frame, bounds, red: 50, green: 50, blue: 50);
            PaintTextContrast(frame, bounds, red: 220, green: 220, blue: 220);
        }
        else
        {
            PaintRectangle(frame, bounds, red: 84, green: 210, blue: 139);
            PaintTextContrast(frame, bounds, red: 30, green: 30, blue: 30);
        }
    }

    private static void PaintTextContrast(
        CapturedFrame frame,
        CapturePixelRect bounds,
        byte red,
        byte green,
        byte blue)
    {
        var insetX = Math.Max(4, bounds.Width / 4);
        var insetY = Math.Max(4, bounds.Height / 3);
        PaintRectangle(
            frame,
            new CapturePixelRect(
                bounds.X + insetX,
                bounds.Y + insetY,
                Math.Max(4, bounds.Width / 5),
                Math.Max(4, bounds.Height / 5)),
            red,
            green,
            blue);
    }

    private static void PaintRectangle(
        CapturedFrame frame,
        CapturePixelRect bounds,
        byte red,
        byte green,
        byte blue)
    {
        for (var y = bounds.Y; y < bounds.Bottom; y++)
        {
            for (var x = bounds.X; x < bounds.Right; x++)
            {
                var offset = checked((y * frame.Stride) + (x * 4));
                frame.Bgra32Pixels[offset] = blue;
                frame.Bgra32Pixels[offset + 1] = green;
                frame.Bgra32Pixels[offset + 2] = red;
                frame.Bgra32Pixels[offset + 3] = 255;
            }
        }
    }
}
