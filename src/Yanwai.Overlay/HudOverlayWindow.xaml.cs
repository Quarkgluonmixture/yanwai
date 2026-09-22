using System.Windows;

namespace Yanwai.Overlay;

public partial class HudOverlayWindow : Window
{
    public HudOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => OverlayNative.MakeClickThrough(this);
    }

    /// <summary>
    /// Sets the content and returns the height it needs at <paramref name="widthDips"/>,
    /// in DIPs. The layout has to know a card's height before placing it: a tall card
    /// that fits beside one bubble may cover the next.
    /// </summary>
    public double SetContent(OverlayContent content, double widthDips)
    {
        SectionList.ItemsSource = content.Sections;
        Root.Measure(new Size(widthDips, double.PositiveInfinity));
        return Math.Ceiling(Root.DesiredSize.Height);
    }

    public void SetPhysicalBounds(int x, int y, int width, int height) =>
        OverlayNative.SetPhysicalBounds(this, x, y, width, height);
}
