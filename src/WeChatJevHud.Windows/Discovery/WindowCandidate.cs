using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Windows.Discovery;

public sealed record WindowCandidate(
    nint Handle,
    uint ProcessId,
    string ProcessName,
    string Title,
    string ClassName,
    DesktopPixelRect Bounds,
    bool IsVisible,
    bool IsMinimized,
    bool IsOwned,
    bool IsCloaked,
    bool HasRenderChild);
