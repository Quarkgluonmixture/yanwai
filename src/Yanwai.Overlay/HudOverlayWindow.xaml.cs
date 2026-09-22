using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Yanwai.Overlay;

public partial class HudOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    public HudOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void SetContent(OverlayContent content)
    {
        MessageText.Text = content.Message;
        HeadlineText.Text = content.Headline;
        AdviceText.Text = content.Advice;
        RowList.ItemsSource = content.Rows;
    }

    /// <summary>
    /// Makes the window click-through and keeps it out of the taskbar and Alt+Tab.
    /// Without WS_EX_TRANSPARENT the HUD would swallow clicks meant for WeChat; without
    /// WS_EX_NOACTIVATE, showing it would steal focus from whatever the user is typing
    /// into.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    /// <summary>
    /// Positions the window in physical desktop pixels.
    ///
    /// WPF's Left/Top/Width are DIPs whose reference monitor is ambiguous while the
    /// window is being moved across monitors with different scales, which is exactly
    /// the case this HUD has to survive. SetWindowPos takes physical pixels under
    /// Per-Monitor V2, so the placement stays the one the geometry computed; WPF then
    /// lays the content out in that monitor's DIPs on its own.
    /// </summary>
    public void SetPhysicalBounds(int x, int y, int width, int height)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        SetWindowPos(handle, HwndTopmost, x, y, width, height, SwpNoActivate);
    }

    private const int SwpNoActivate = 0x0010;
    private static readonly nint HwndTopmost = -1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint window, int index, int value);
}
