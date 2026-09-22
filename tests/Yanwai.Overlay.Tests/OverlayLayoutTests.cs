using Yanwai.Core.Geometry;
using Yanwai.Overlay;
using Xunit;

namespace Yanwai.Overlay.Tests;

public sealed class OverlayLayoutTests
{
    private static readonly CapturePixelRect Chat = new(300, 60, 900, 900);
    private static readonly DesktopPixelRect Frame = new(100, 50, 1300, 1000);
    private static readonly DesktopPixelRect WorkArea = new(0, 0, 2560, 1400);
    private static readonly (int, int) Card = (390, 252);
    private static readonly (int, int) Chip = (345, 39);

    private static IReadOnlyList<HudSlot> Arrange(
        string? selected,
        Dictionary<string, CapturePixelRect> visible,
        params string[] judged) =>
        OverlayLayout.Arrange(judged, selected, visible, Chat, Frame, WorkArea, Card, Chip, gap: 18);

    [Fact]
    public void Selected_gets_the_card_and_the_others_get_chips()
    {
        var slots = Arrange(
            "b",
            new() { ["a"] = new(320, 100, 300, 60), ["b"] = new(320, 600, 300, 60) },
            "a", "b");

        Assert.Equal(HudKind.Card, Assert.Single(slots, s => s.Id == "b").Kind);
        Assert.Equal(HudKind.Chip, Assert.Single(slots, s => s.Id == "a").Kind);
    }

    [Fact]
    public void A_chip_that_would_sit_under_the_card_is_left_out()
    {
        // b's card starts at b's top and runs 252 px down, over c's chip.
        var slots = Arrange(
            "b",
            new() { ["b"] = new(320, 400, 300, 60), ["c"] = new(320, 480, 300, 60) },
            "b", "c");

        Assert.Equal("b", Assert.Single(slots).Id);
    }

    [Fact]
    public void Judged_messages_that_are_not_on_screen_get_nothing()
    {
        var slots = Arrange(
            "gone",
            new() { ["a"] = new(320, 100, 300, 60), ["half"] = new(320, 940, 300, 60) },
            "gone", "a", "half");

        // "gone" scrolled away, "half" is mostly below the chat area: only "a" remains,
        // and with the selection off screen it stays a chip rather than becoming a card.
        var slot = Assert.Single(slots);
        Assert.Equal(("a", HudKind.Chip), (slot.Id, slot.Kind));
    }
}
