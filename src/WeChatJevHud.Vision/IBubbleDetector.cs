using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;

namespace WeChatJevHud.Vision;

public interface IBubbleDetector
{
    IReadOnlyList<DetectedBubble> Detect(CapturedFrame frame, CapturePixelRect chatRegion);
}

public sealed record DetectedBubble(CapturePixelRect Bounds, MessageSide Side, double DetectionScore);
