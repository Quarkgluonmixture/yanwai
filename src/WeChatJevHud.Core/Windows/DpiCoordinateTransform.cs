using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Core.Windows;

/// <summary>
/// Converts physical virtual-desktop pixels to monitor-relative WPF DIPs and back.
/// A new transform must be taken from each refreshed window snapshot after a
/// monitor or DPI change.
/// </summary>
public sealed class DpiCoordinateTransform
{
    private readonly MonitorSnapshot _monitor;
    private readonly DpiSnapshot _dpi;

    public DpiCoordinateTransform(MonitorSnapshot monitor, DpiSnapshot dpi)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (dpi.X == 0 || dpi.Y == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), "DPI values must be positive.");
        }

        _monitor = monitor;
        _dpi = dpi;
    }

    public MonitorDipRect DesktopPixelsToMonitorDips(DesktopPixelRect pixels) =>
        new(
            (pixels.X - (double)_monitor.Bounds.X) / _dpi.ScaleX,
            (pixels.Y - (double)_monitor.Bounds.Y) / _dpi.ScaleY,
            pixels.Width / _dpi.ScaleX,
            pixels.Height / _dpi.ScaleY);

    public DesktopPixelRect MonitorDipsToDesktopPixels(MonitorDipRect dips) =>
        new(
            AddAndRound(_monitor.Bounds.X, dips.X * _dpi.ScaleX),
            AddAndRound(_monitor.Bounds.Y, dips.Y * _dpi.ScaleY),
            Round(dips.Width * _dpi.ScaleX),
            Round(dips.Height * _dpi.ScaleY));

    private static int AddAndRound(int origin, double value) => checked(origin + Round(value));

    private static int Round(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
}
