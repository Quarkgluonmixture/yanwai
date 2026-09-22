using System.Windows;
using System.Windows.Media;

namespace Yanwai.Overlay;

/// <summary>One-line HUD for a judged message that is not the selected one.</summary>
public partial class HudChipWindow : Window
{
    /// <summary>Danger levels 0-3, from "no problem" to "about to turn into a fight".</summary>
    private static readonly Brush[] SeverityBrushes =
    [
        Frozen("#5FBF8F"),
        Frozen("#E8C94B"),
        Frozen("#E8904B"),
        Frozen("#E0605A"),
    ];

    /// <summary>No danger answer came back. Grey, not green: absent is not "safe".</summary>
    private static readonly Brush UnknownBrush = Frozen("#8C8C94");

    public HudChipWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => OverlayNative.MakeClickThrough(this);
    }

    public void SetContent(OverlayChip chip)
    {
        ChipText.Text = chip.Text;
        SeverityDot.Fill = chip.Severity is { } level
            ? SeverityBrushes[Math.Clamp(level, 0, SeverityBrushes.Length - 1)]
            : UnknownBrush;
    }

    public void SetPhysicalBounds(int x, int y, int width, int height) =>
        OverlayNative.SetPhysicalBounds(this, x, y, width, height);

    private static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
