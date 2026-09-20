using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Vision;

public interface IChatRegionLocator
{
    DetectedChatRegion Locate(CapturedFrame frame);
}

public sealed record DetectedChatRegion(CapturePixelRect Bounds, double Confidence);
