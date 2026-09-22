using Yanwai.Core.Geometry;
using Yanwai.Overlay;
using Xunit;

namespace Yanwai.Overlay.Tests;

public sealed class OverlayLayoutTests
{
    // Frame origin at (0,0) so capture and desktop coordinates coincide.
    private static readonly CapturePixelRect Chat = new(300, 0, 900, 1000);
    private static readonly DesktopPixelRect Frame = new(0, 0, 1300, 1100);
    private static readonly DesktopPixelRect WorkArea = new(0, 0, 2560, 1400);
    private static readonly (int, int) Chip = (300, 40);

    private static IReadOnlyList<HudSlot> Arrange(
        string? selected,
        Dictionary<string, CapturePixelRect> visible,
        params string[] judged) =>
        OverlayLayout.Arrange(
            judged.Select(id => new HudRequest(id, 360, 200)).ToList(),
            selected, visible, Chat, Frame, WorkArea, Chip, gap: 12);

    [Fact]
    public void A_card_goes_under_its_bubble_when_the_gap_is_free()
    {
        var slot = Assert.Single(Arrange(null, new() { ["a"] = new(320, 100, 300, 60) }, "a"));

        Assert.Equal(HudKind.Card, slot.Kind);
        Assert.Equal(320, slot.Bounds.X);
        Assert.True(slot.Bounds.Y >= 160);
    }

    [Fact]
    public void A_card_moves_beside_its_bubble_rather_than_cover_the_next_message()
    {
        var slots = Arrange(
            null,
            new() { ["a"] = new(320, 100, 300, 60), ["next"] = new(320, 200, 200, 60) },
            "a");

        var slot = Assert.Single(slots);
        Assert.Equal(HudKind.Card, slot.Kind);
        Assert.True(slot.Bounds.X >= 620, "beside the bubble, not under it");
        Assert.False(OverlayLayout.Intersects(slot.Bounds, new DesktopPixelRect(320, 200, 200, 60)));
    }

    [Fact]
    public void With_no_room_for_a_card_it_falls_back_to_a_chip_and_never_covers_a_bubble()
    {
        // A wide self message just under "a" blocks both the gap below and the space beside it.
        var visible = new Dictionary<string, CapturePixelRect>
        {
            ["a"] = new(320, 100, 300, 60),
            ["wide"] = new(400, 180, 780, 60),
        };

        var slot = Assert.Single(Arrange(null, visible, "a"));

        Assert.Equal(HudKind.Chip, slot.Kind);
        Assert.All(visible.Values, bubble =>
            Assert.False(OverlayLayout.Intersects(slot.Bounds, new DesktopPixelRect(bubble.X, bubble.Y, bubble.Width, bubble.Height)), $"covers {bubble}"));
    }

    [Fact]
    public void Cards_never_overlap_each_other_and_the_selected_one_wins_the_space()
    {
        var visible = new Dictionary<string, CapturePixelRect>
        {
            ["old"] = new(320, 100, 300, 60),
            ["new"] = new(320, 400, 300, 60),
        };

        var slots = Arrange("old", visible, "old", "new");

        Assert.Equal(HudKind.Card, Assert.Single(slots, s => s.Id == "old").Kind);
        for (var i = 0; i < slots.Count; i++)
        {
            for (var j = i + 1; j < slots.Count; j++)
            {
                Assert.False(OverlayLayout.Intersects(slots[i].Bounds, slots[j].Bounds));
            }
        }
    }

    [Fact]
    public void Judged_messages_that_are_not_on_screen_get_nothing()
    {
        var slots = Arrange(
            "gone",
            new() { ["a"] = new(320, 100, 300, 60), ["half"] = new(320, 960, 300, 100) },
            "gone", "a", "half");

        Assert.Equal("a", Assert.Single(slots).Id);
    }
}
