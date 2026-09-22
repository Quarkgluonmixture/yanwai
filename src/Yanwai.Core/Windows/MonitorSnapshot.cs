using Yanwai.Core.Geometry;

namespace Yanwai.Core.Windows;

public sealed record MonitorSnapshot(
    string DeviceName,
    DesktopPixelRect Bounds,
    DesktopPixelRect WorkArea,
    bool IsPrimary);
