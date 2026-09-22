using System.Windows;

namespace Yanwai.Overlay;

public partial class HudOverlayWindow : Window
{
    public HudOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => OverlayNative.MakeClickThrough(this);
    }

    public void SetContent(OverlayContent content)
    {
        MessageText.Text = content.Message;
        HeadlineText.Text = content.Headline;
        AdviceText.Text = content.Advice;
        RowList.ItemsSource = content.Rows;
    }

    public void SetPhysicalBounds(int x, int y, int width, int height) =>
        OverlayNative.SetPhysicalBounds(this, x, y, width, height);
}
