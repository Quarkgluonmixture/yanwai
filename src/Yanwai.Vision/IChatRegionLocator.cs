using Yanwai.Capture;
using Yanwai.Core.Geometry;

namespace Yanwai.Vision;

public interface IChatRegionLocator
{
    DetectedChatRegion Locate(CapturedFrame frame);
}

public sealed record DetectedChatRegion(CapturePixelRect Bounds, double DetectionScore);
