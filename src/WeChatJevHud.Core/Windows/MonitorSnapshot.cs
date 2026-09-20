using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Core.Windows;

public sealed record MonitorSnapshot(
    string DeviceName,
    DesktopPixelRect Bounds,
    DesktopPixelRect WorkArea,
    bool IsPrimary);
