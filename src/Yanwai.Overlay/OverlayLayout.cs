using Yanwai.Core.Geometry;

namespace Yanwai.Overlay;

public enum HudKind
{
    Card,
    Chip,
}

public sealed record HudSlot(string Id, HudKind Kind, DesktopPixelRect Bounds);

/// <summary>
/// Decides which judged messages get a HUD this frame and where. Pure geometry, like
/// <see cref="OverlayPlacement"/>, so the rules are testable without windows.
///
/// The selected message gets the full card; every other judged message on screen gets
/// a one-line chip. A chip that would sit under the card is left out rather than
/// drawn half-hidden.
/// </summary>
public static class OverlayLayout
{
    public static IReadOnlyList<HudSlot> Arrange(
        IEnumerable<string> judgedIds,
        string? selectedId,
        IReadOnlyDictionary<string, CapturePixelRect> visibleBubbles,
        CapturePixelRect chatRegion,
        DesktopPixelRect frameBounds,
        DesktopPixelRect workArea,
        (int Width, int Height) cardSize,
        (int Width, int Height) chipSize,
        int gap)
    {
        ArgumentNullException.ThrowIfNull(judgedIds);
        ArgumentNullException.ThrowIfNull(visibleBubbles);

        var slots = new List<HudSlot>();
        DesktopPixelRect? card = null;

        if (selectedId is not null && TryAnchor(selectedId, out var selectedAnchor))
        {
            card = OverlayPlacement.Place(selectedAnchor, cardSize, workArea, gap).Bounds;
            slots.Add(new HudSlot(selectedId, HudKind.Card, card.Value));
        }

        foreach (var id in judgedIds)
        {
            if (string.Equals(id, selectedId, StringComparison.Ordinal) || !TryAnchor(id, out var anchor))
            {
                continue;
            }

            var chip = OverlayPlacement.Place(anchor, chipSize, workArea, gap).Bounds;
            if (card is { } cardBounds && Intersects(cardBounds, chip))
            {
                continue;
            }

            slots.Add(new HudSlot(id, HudKind.Chip, chip));
        }

        return slots;

        bool TryAnchor(string id, out DesktopPixelRect anchor)
        {
            if (visibleBubbles.TryGetValue(id, out var bubble) && OverlayPlacement.IsAnchorVisible(bubble, chatRegion))
            {
                anchor = OverlayPlacement.CaptureToDesktop(bubble, frameBounds);
                return true;
            }

            anchor = default;
            return false;
        }
    }

    public static bool Intersects(DesktopPixelRect a, DesktopPixelRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width &&
        a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
