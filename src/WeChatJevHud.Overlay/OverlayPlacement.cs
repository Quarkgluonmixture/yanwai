using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Overlay;

public enum OverlaySide
{
    Right,
    Left,
    Below,
}

public sealed record OverlayPlacementResult(DesktopPixelRect Bounds, OverlaySide Side);

/// <summary>
/// Decides where the HUD sits relative to the message it belongs to. Pure integer
/// geometry in physical desktop pixels: no WPF, no window handles, so the rules can be
/// tested without a screen.
/// </summary>
public static class OverlayPlacement
{
    /// <summary>Gap between the bubble and the HUD, in physical pixels at 100% scale.</summary>
    public const int GapAtUnitScale = 12;

    /// <summary>
    /// Converts a rectangle measured inside a captured frame into desktop pixels.
    /// The frame is captured 1:1, so this is the frame's own origin plus the offset.
    /// </summary>
    public static DesktopPixelRect CaptureToDesktop(CapturePixelRect rect, DesktopPixelRect frameBounds) =>
        new(
            checked(frameBounds.X + rect.X),
            checked(frameBounds.Y + rect.Y),
            rect.Width,
            rect.Height);

    /// <summary>
    /// Places a HUD of <paramref name="size"/> beside <paramref name="anchor"/>, inside
    /// <paramref name="workArea"/>.
    ///
    /// Right of the message is the normal position. When it does not fit there the HUD
    /// goes left, and only if neither side fits does it drop below. It is never allowed
    /// to overlap the anchor: covering the message it is explaining is worse than an
    /// unusual position.
    /// </summary>
    public static OverlayPlacementResult Place(
        DesktopPixelRect anchor,
        (int Width, int Height) size,
        DesktopPixelRect workArea,
        int gap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size.Height);
        ArgumentOutOfRangeException.ThrowIfNegative(gap);

        var anchorRight = checked(anchor.X + anchor.Width);
        var workRight = checked(workArea.X + workArea.Width);

        var rightX = checked(anchorRight + gap);
        if (checked(rightX + size.Width) <= workRight)
        {
            return new OverlayPlacementResult(
                new DesktopPixelRect(rightX, ClampTop(anchor.Y, size.Height, workArea), size.Width, size.Height),
                OverlaySide.Right);
        }

        var leftX = checked(anchor.X - gap - size.Width);
        if (leftX >= workArea.X)
        {
            return new OverlayPlacementResult(
                new DesktopPixelRect(leftX, ClampTop(anchor.Y, size.Height, workArea), size.Width, size.Height),
                OverlaySide.Left);
        }

        var belowY = checked(anchor.Y + anchor.Height + gap);
        var x = Clamp(anchor.X, workArea.X, Math.Max(workArea.X, checked(workRight - size.Width)));
        return new OverlayPlacementResult(
            new DesktopPixelRect(x, ClampTop(belowY, size.Height, workArea), size.Width, size.Height),
            OverlaySide.Below);
    }

    /// <summary>
    /// True when the anchor is at least partly inside the region the HUD may point at.
    /// A bubble scrolled out of the chat area must not keep a HUD pinned to the edge.
    /// </summary>
    public static bool IsAnchorVisible(CapturePixelRect anchor, CapturePixelRect chatRegion)
    {
        if (anchor.IsEmpty || chatRegion.IsEmpty)
        {
            return false;
        }

        var overlapWidth = Math.Min(anchor.Right, chatRegion.Right) - Math.Max(anchor.X, chatRegion.X);
        var overlapHeight = Math.Min(anchor.Bottom, chatRegion.Bottom) - Math.Max(anchor.Y, chatRegion.Y);
        if (overlapWidth <= 0 || overlapHeight <= 0)
        {
            return false;
        }

        // Require most of the bubble's height: a sliver peeking past the edge is not
        // something the HUD should still be explaining.
        return overlapHeight * 2 >= anchor.Height;
    }

    private static int ClampTop(int desiredTop, int height, DesktopPixelRect workArea)
    {
        var lowest = checked(workArea.Y + workArea.Height - height);
        return Clamp(desiredTop, workArea.Y, Math.Max(workArea.Y, lowest));
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
}
