using System.Diagnostics;
using WeChatJevHud.Capture;

namespace WeChatJevHud.Vision;

public sealed class BubbleDetectionPipeline
{
    private readonly IChatRegionLocator _chatRegionLocator;
    private readonly IBubbleDetector _bubbleDetector;

    public BubbleDetectionPipeline(IChatRegionLocator chatRegionLocator, IBubbleDetector bubbleDetector)
    {
        _chatRegionLocator = chatRegionLocator;
        _bubbleDetector = bubbleDetector;
    }

    public BubbleDetectionResult Analyze(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var timer = Stopwatch.StartNew();
        var chatRegion = _chatRegionLocator.Locate(frame);
        var bubbles = _bubbleDetector.Detect(frame, chatRegion.Bounds);
        timer.Stop();
        return new BubbleDetectionResult(chatRegion, bubbles, timer.Elapsed);
    }
}

public sealed record BubbleDetectionResult(
    DetectedChatRegion ChatRegion,
    IReadOnlyList<DetectedBubble> Bubbles,
    TimeSpan Duration);
