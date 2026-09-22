using Yanwai.Capture;
using Yanwai.Core.Geometry;
using Yanwai.Core.Messages;

namespace Yanwai.Vision;

public interface IBubbleDetector
{
    IReadOnlyList<DetectedBubble> Detect(CapturedFrame frame, CapturePixelRect chatRegion);
}

public sealed record DetectedBubble(CapturePixelRect Bounds, MessageSide Side, double DetectionScore);
