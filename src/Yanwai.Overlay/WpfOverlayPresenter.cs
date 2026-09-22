using System.Windows;
using Yanwai.Core.Geometry;
using Yanwai.Core.Windows;

namespace Yanwai.Overlay;

/// <summary>
/// Drives a single <see cref="HudOverlayWindow"/>. Must be constructed on the UI thread.
///
/// The anchor is kept in capture-frame coordinates and re-projected on every
/// <see cref="Follow"/>, so moving or resizing WeChat, or dragging it to a monitor with
/// a different scale, repositions the HUD without a new judgment.
/// </summary>
public sealed class WpfOverlayPresenter : IOverlayPresenter, IDisposable
{
    /// <summary>Collapsed size in DIPs. Deliberately small: this is a glance, not a report.</summary>
    private const double WidthDips = 260;
    private const double HeightDips = 168;

    private readonly HudOverlayWindow _window = new();

    private CapturePixelRect? _anchor;
    private CapturePixelRect _chatRegion;
    private bool _hasContent;
    private WeChatWindowSnapshot? _lastSnapshot;

    public void Show(OverlayContent content, CapturePixelRect anchor, CapturePixelRect chatRegion)
    {
        ArgumentNullException.ThrowIfNull(content);

        _window.SetContent(content);
        _anchor = anchor;
        _chatRegion = chatRegion;
        _hasContent = true;
    }

    public void Reanchor(CapturePixelRect? anchor, CapturePixelRect chatRegion)
    {
        if (!_hasContent)
        {
            return;
        }

        _anchor = anchor;
        _chatRegion = chatRegion;
        Follow(_lastSnapshot);
    }

    public void Follow(WeChatWindowSnapshot? snapshot)
    {
        _lastSnapshot = snapshot;
        if (!_hasContent)
        {
            return;
        }

        if (_anchor is not { } anchor)
        {
            // Content is kept; only the position is unknown right now.
            HideWindow();
            return;
        }

        // Topmost plus "WeChat is somewhere behind" would leave the HUD sitting over
        // whatever the user actually switched to. It belongs on screen only while the
        // conversation it annotates is the thing being looked at.
        if (snapshot is null || !snapshot.IsVisible || snapshot.IsMinimized || !snapshot.IsForeground)
        {
            HideWindow();
            return;
        }

        if (!OverlayPlacement.IsAnchorVisible(anchor, _chatRegion))
        {
            HideWindow();
            return;
        }

        var transform = snapshot.CoordinateTransform;
        var size = transform.MonitorDipsToDesktopPixels(new MonitorDipRect(0, 0, WidthDips, HeightDips));
        var gap = Math.Max(1, (int)Math.Round(OverlayPlacement.GapAtUnitScale * snapshot.Dpi.ScaleX));

        var anchorDesktop = OverlayPlacement.CaptureToDesktop(anchor, snapshot.CaptureBounds);
        var placement = OverlayPlacement.Place(
            anchorDesktop,
            (size.Width, size.Height),
            snapshot.Monitor.WorkArea,
            gap);

        if (!_window.IsVisible)
        {
            // Show first: the window needs a handle before it can be positioned, and
            // ShowActivated is false so this does not take focus.
            _window.Show();
        }

        _window.SetPhysicalBounds(
            placement.Bounds.X,
            placement.Bounds.Y,
            placement.Bounds.Width,
            placement.Bounds.Height);
    }

    public void Hide()
    {
        _hasContent = false;
        _anchor = null;
        HideWindow();
    }

    private void HideWindow()
    {
        if (_window.IsVisible)
        {
            _window.Hide();
        }
    }

    public void Dispose() => _window.Close();
}
