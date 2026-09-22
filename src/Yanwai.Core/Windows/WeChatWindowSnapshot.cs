using Yanwai.Core.Geometry;

namespace Yanwai.Core.Windows;

public sealed record WeChatWindowSnapshot(
    nint Handle,
    uint ProcessId,
    string ProcessName,
    string Title,
    string ClassName,
    DesktopPixelRect TopLevelBounds,
    DesktopPixelRect ClientBounds,
    nint? RenderHandle,
    DesktopPixelRect? RenderBounds,
    MonitorSnapshot Monitor,
    DpiSnapshot Dpi,
    bool IsVisible,
    bool IsMinimized,
    bool IsForeground,
    DateTimeOffset ObservedAt)
{
    public DpiCoordinateTransform CoordinateTransform => new(Monitor, Dpi);

    public DesktopPixelRect CaptureBounds
    {
        get
        {
            if (RenderBounds is { IsEmpty: false } renderBounds)
            {
                return renderBounds;
            }

            return ClientBounds.IsEmpty ? TopLevelBounds : ClientBounds;
        }
    }
}
