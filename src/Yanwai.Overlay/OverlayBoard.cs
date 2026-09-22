using System.Windows;
using Yanwai.Core.Geometry;
using Yanwai.Core.Windows;

namespace Yanwai.Overlay;

/// <summary>
/// All HUDs for the conversation on screen: one full card for the selected message and
/// a chip for every other judged message. Must be used on the UI thread.
///
/// Positions are never cached in desktop space. Every input (window state, bubble
/// positions, selection, content) re-runs <see cref="OverlayLayout.Arrange"/>, so a
/// move, resize, DPI change or scroll all go through the same path.
/// </summary>
public sealed class OverlayBoard : IDisposable
{
    /// <summary>Sizes in DIPs. The card is a glance, the chip is a word.</summary>
    private const double CardWidthDips = 260;
    private const double CardHeightDips = 168;
    private const double ChipWidthDips = 230;
    private const double ChipHeightDips = 26;

    /// <summary>Oldest judgments are dropped past this; they are long scrolled away.</summary>
    private const int MaxEntries = 60;

    private readonly HudOverlayWindow _card = new();
    private readonly Dictionary<string, (OverlayContent Card, OverlayChip Chip)> _content = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, HudChipWindow> _chips = new(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, CapturePixelRect> _visible = new Dictionary<string, CapturePixelRect>();
    private CapturePixelRect _chatRegion;
    private bool _captureIsClean;
    private WeChatWindowSnapshot? _snapshot;
    private string? _selectedId;
    private string? _cardShowsId;

    public void Set(string id, OverlayContent card, OverlayChip chip)
    {
        if (!_content.ContainsKey(id))
        {
            _order.Add(id);
        }

        _content[id] = (card, chip);
        if (string.Equals(_cardShowsId, id, StringComparison.Ordinal))
        {
            _cardShowsId = null;
        }

        if (_chips.TryGetValue(id, out var window))
        {
            window.SetContent(chip);
        }

        while (_order.Count > MaxEntries)
        {
            Remove(_order[0]);
        }

        Render();
    }

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
        Render();
    }

    private void Remove(string id)
    {
        _order.Remove(id);
        _content.Remove(id);
        if (_chips.Remove(id, out var window))
        {
            window.Close();
        }
    }

    private void Render()
    {
        var snapshot = _snapshot;
        if (snapshot is null || !snapshot.IsVisible || snapshot.IsMinimized || !snapshot.IsForeground
            || !_captureIsClean || _content.Count == 0)
        {
            HideAll();
            return;
        }

        var transform = snapshot.CoordinateTransform;
        var card = transform.MonitorDipsToDesktopPixels(new MonitorDipRect(0, 0, CardWidthDips, CardHeightDips));
        var chip = transform.MonitorDipsToDesktopPixels(new MonitorDipRect(0, 0, ChipWidthDips, ChipHeightDips));
        var gap = Math.Max(1, (int)Math.Round(OverlayPlacement.GapAtUnitScale * snapshot.Dpi.ScaleX));

        var selected = _selectedId is not null && _content.ContainsKey(_selectedId) ? _selectedId : null;
        var slots = OverlayLayout.Arrange(
            _order,
            selected,
            _visible,
            _chatRegion,
            snapshot.CaptureBounds,
            snapshot.Monitor.WorkArea,
            (card.Width, card.Height),
            (chip.Width, chip.Height),
            gap);

        var cardShown = false;
        var chipsShown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (slot.Kind == HudKind.Card)
            {
                if (!string.Equals(_cardShowsId, slot.Id, StringComparison.Ordinal))
                {
                    _card.SetContent(_content[slot.Id].Card);
                    _cardShowsId = slot.Id;
                }

                ShowAt(_card, slot.Bounds, _card.SetPhysicalBounds);
                cardShown = true;
                continue;
            }

            if (!_chips.TryGetValue(slot.Id, out var window))
            {
                window = new HudChipWindow();
                window.SetContent(_content[slot.Id].Chip);
                _chips[slot.Id] = window;
            }

            ShowAt(window, slot.Bounds, window.SetPhysicalBounds);
            chipsShown.Add(slot.Id);
        }

        if (!cardShown && _card.IsVisible)
        {
            _card.Hide();
        }

        foreach (var (id, window) in _chips)
        {
            if (!chipsShown.Contains(id) && window.IsVisible)
            {
                window.Hide();
            }
        }
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
        if (_card.IsVisible)
        {
            _card.Hide();
        }

        foreach (var window in _chips.Values)
        {
            if (window.IsVisible)
            {
                window.Hide();
            }
        }
    }

    public void Dispose()
    {
        _card.Close();
        foreach (var window in _chips.Values)
        {
            window.Close();
        }

        _chips.Clear();
    }
}
