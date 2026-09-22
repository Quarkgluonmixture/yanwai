using Yanwai.Core.Geometry;

namespace Yanwai.Overlay;

public enum HudKind
{
    Card,
    Chip,
}

/// <summary>A judged message and the size its card needs, in physical pixels.</summary>
public sealed record HudRequest(string Id, int CardWidth, int CardHeight);

public sealed record HudSlot(string Id, HudKind Kind, DesktopPixelRect Bounds);

/// <summary>
/// Decides which judged messages get a HUD this frame, what kind and where. Pure
/// geometry, like <see cref="OverlayPlacement"/>, so the rules are testable without
/// windows.
///
/// WeChat's own layout cannot be changed (no injection), so a card can only go where
/// nothing is: directly under its bubble when the gap allows, otherwise beside it.
/// A card never covers a bubble — its own or anyone else's — or another card; when
/// neither spot is free it shrinks to a one-line chip, and when even that collides it
/// is left out. The selected message is placed first, then newest to oldest.
/// </summary>
public static class OverlayLayout
{
    public static IReadOnlyList<HudSlot> Arrange(
        IReadOnlyList<HudRequest> requests,
        string? selectedId,
        IReadOnlyDictionary<string, CapturePixelRect> visibleBubbles,
        CapturePixelRect chatRegion,
        DesktopPixelRect frameBounds,
        DesktopPixelRect workArea,
        (int Width, int Height) chipSize,
        int gap)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(visibleBubbles);

        var chat = OverlayPlacement.CaptureToDesktop(chatRegion, frameBounds);
        var obstacles = visibleBubbles.Values
            .Select(bubble => OverlayPlacement.CaptureToDesktop(bubble, frameBounds))
            .ToList();

        var ordered = requests
            .Where(request => visibleBubbles.TryGetValue(request.Id, out var bubble)
                              && OverlayPlacement.IsAnchorVisible(bubble, chatRegion))
            .OrderByDescending(request => string.Equals(request.Id, selectedId, StringComparison.Ordinal))
            .ThenByDescending(request => visibleBubbles[request.Id].Y);

        var slots = new List<HudSlot>();
        foreach (var request in ordered)
        {
            var anchor = OverlayPlacement.CaptureToDesktop(visibleBubbles[request.Id], frameBounds);
            var card = (request.CardWidth, request.CardHeight);

            // Under the bubble, as if written into the chat. Only inside the chat area:
            // below it is the input box.
            var below = new DesktopPixelRect(anchor.X, anchor.Y + anchor.Height + gap / 2, card.CardWidth, card.CardHeight);
            if (Contains(chat, below) && IsFree(below))
            {
                slots.Add(new HudSlot(request.Id, HudKind.Card, below));
                continue;
            }

            var right = Beside(anchor, card);
            if (right is { } besideCard && IsFree(besideCard))
            {
                slots.Add(new HudSlot(request.Id, HudKind.Card, besideCard));
                continue;
            }

            if (Beside(anchor, chipSize) is { } chip && IsFree(chip))
            {
                slots.Add(new HudSlot(request.Id, HudKind.Chip, chip));
            }
        }

        return slots;

        DesktopPixelRect? Beside(DesktopPixelRect anchor, (int Width, int Height) size)
        {
            var rect = new DesktopPixelRect(anchor.X + anchor.Width + gap, anchor.Y, size.Width, size.Height);
            return Contains(workArea, rect) ? rect : null;
        }

        bool IsFree(DesktopPixelRect rect) =>
            !obstacles.Any(obstacle => Intersects(obstacle, rect)) &&
            !slots.Any(slot => Intersects(slot.Bounds, rect));
    }

    public static bool Intersects(DesktopPixelRect a, DesktopPixelRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width &&
        a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static bool Contains(DesktopPixelRect outer, DesktopPixelRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.X + inner.Width <= outer.X + outer.Width &&
        inner.Y + inner.Height <= outer.Y + outer.Height;
}
