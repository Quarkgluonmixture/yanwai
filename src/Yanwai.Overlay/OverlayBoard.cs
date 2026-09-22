using System.Windows;
using Yanwai.Core.Geometry;
using Yanwai.Core.Windows;

namespace Yanwai.Overlay;

/// <summary>
/// All HUDs for the conversation on screen: a card for every judged message that has
/// room for one, a one-line chip for those that do not. Must be used on the UI thread.
///
/// Positions are never cached in desktop space. Every input (window state, bubble
/// positions, selection, content) re-runs <see cref="OverlayLayout.Arrange"/>, so a
/// move, resize, DPI change or scroll all go through the same path.
/// </summary>
public sealed class OverlayBoard : IDisposable
{
    /// <summary>Widths in DIPs; a card's height comes from its content.</summary>
    private const double CardWidthDips = 240;
    private const double ChipWidthDips = 230;
    private const double ChipHeightDips = 26;

    /// <summary>Oldest judgments are dropped past this; they are long scrolled away.</summary>
    private const int MaxEntries = 60;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    private IReadOnlyDictionary<string, CapturePixelRect> _visible = new Dictionary<string, CapturePixelRect>();
    private CapturePixelRect _chatRegion;
    private bool _captureIsClean;
    private WeChatWindowSnapshot? _snapshot;
    private string? _selectedId;

    public void Set(string id, OverlayContent card, OverlayChip chip)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            entry = new Entry();
            _entries[id] = entry;
            _order.Add(id);
        }

        entry.CardHeightDips = entry.Card.SetContent(card, CardWidthDips);
        entry.ChipContent = chip;
        entry.Chip?.SetContent(chip);

        while (_order.Count > MaxEntries)
        {
            Remove(_order[0]);
        }

        Render();
    }

    /// <summary>The selected message is placed first, so it gets its card whenever any fits.</summary>
    public void Select(string? id)
    {
        _selectedId = id;
        Render();
    }

    public void UpdateAnchors(
        IReadOnlyDictionary<string, CapturePixelRect> visibleBubbles,
        CapturePixelRect chatRegion,
        bool captureIsClean)
    {
        _visible = visibleBubbles;
        _chatRegion = chatRegion;
        _captureIsClean = captureIsClean;
        Render();
    }

    /// <summary>
    /// A null snapshot, or a hidden, minimized or background WeChat hides every HUD:
    /// Topmost alone would leave them over whatever the user switched to (GOTCHAS 5).
    /// </summary>
    public void Follow(WeChatWindowSnapshot? snapshot)
    {
        _snapshot = snapshot;
        Render();
    }

    public void Clear()
    {
        foreach (var id in _order.ToArray())
        {
            Remove(id);
        }

        _selectedId = null;
    }

    private void Remove(string id)
    {
        _order.Remove(id);
        if (_entries.Remove(id, out var entry))
        {
            entry.Close();
        }
    }

    private void Render()
    {
        var snapshot = _snapshot;
        if (snapshot is null || !snapshot.IsVisible || snapshot.IsMinimized || !snapshot.IsForeground
            || !_captureIsClean || _entries.Count == 0)
        {
            HideAll();
            return;
        }

        var transform = snapshot.CoordinateTransform;
        var chip = transform.MonitorDipsToDesktopPixels(new MonitorDipRect(0, 0, ChipWidthDips, ChipHeightDips));
        var gap = Math.Max(1, (int)Math.Round(OverlayPlacement.GapAtUnitScale * snapshot.Dpi.ScaleX));

        var requests = _order
            .Select(id =>
            {
                var size = transform.MonitorDipsToDesktopPixels(
                    new MonitorDipRect(0, 0, CardWidthDips, _entries[id].CardHeightDips));
                return new HudRequest(id, size.Width, size.Height);
            })
            .ToList();

        var slots = OverlayLayout.Arrange(
            requests,
            _selectedId,
            _visible,
            _chatRegion,
            snapshot.CaptureBounds,
            snapshot.Monitor.WorkArea,
            (chip.Width, chip.Height),
            gap);

        var shown = new Dictionary<string, HudKind>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            var entry = _entries[slot.Id];
            if (slot.Kind == HudKind.Card)
            {
                ShowAt(entry.Card, slot.Bounds, entry.Card.SetPhysicalBounds);
            }
            else
            {
                var window = entry.Chip ??= CreateChip(entry.ChipContent!);
                ShowAt(window, slot.Bounds, window.SetPhysicalBounds);
            }

            shown[slot.Id] = slot.Kind;
        }

        foreach (var (id, entry) in _entries)
        {
            var kind = shown.TryGetValue(id, out var k) ? k : (HudKind?)null;
            if (kind != HudKind.Card && entry.Card.IsVisible)
            {
                entry.Card.Hide();
            }

            if (kind != HudKind.Chip && entry.Chip is { IsVisible: true } chipWindow)
            {
                chipWindow.Hide();
            }
        }
    }

    private static HudChipWindow CreateChip(OverlayChip content)
    {
        var window = new HudChipWindow();
        window.SetContent(content);
        return window;
    }

    private static void ShowAt(Window window, DesktopPixelRect bounds, Action<int, int, int, int> setBounds)
    {
        if (!window.IsVisible)
        {
            // Show first: the window needs a handle before it can be positioned, and
            // ShowActivated is false so this does not take focus.
            window.Show();
        }

        setBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private void HideAll()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Card.IsVisible)
            {
                entry.Card.Hide();
            }

            if (entry.Chip is { IsVisible: true } chip)
            {
                chip.Hide();
            }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Close();
        }

        _entries.Clear();
        _order.Clear();
    }

    private sealed class Entry
    {
        public HudOverlayWindow Card { get; } = new();

        public double CardHeightDips { get; set; }

        public HudChipWindow? Chip { get; set; }

        public OverlayChip? ChipContent { get; set; }

        public void Close()
        {
            Card.Close();
            Chip?.Close();
        }
    }
}
