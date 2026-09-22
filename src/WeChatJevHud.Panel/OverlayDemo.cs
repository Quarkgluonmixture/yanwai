using System.Windows.Threading;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Overlay;
using WeChatJevHud.Vision;
using WeChatJevHud.Windows;

namespace WeChatJevHud.Panel;

/// <summary>
/// Debug harness for Phase 6 anchoring (AGENTS.md: anything needing human inspection
/// gets a visible harness). Pins a HUD with fixed content to the last remote bubble on
/// screen and keeps following the window, so move / resize / monitor / DPI / minimize
/// behaviour can be watched without waiting for a real message.
///
/// It calls Jev never, and reads no message text.
/// </summary>
public sealed class OverlayDemo : IDisposable
{
    private readonly WpfOverlayPresenter _overlay = new();
    private readonly Win32WeChatWindowTracker _tracker = new();
    private readonly Win32ScreenRegionCapture _capture = new();
    private readonly IChatRegionLocator _chatRegionLocator = new DarkThemeChatRegionLocator();
    private readonly IBubbleDetector _detector = new DarkThemeBubbleDetector();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public string Start()
    {
        var window = _tracker.Locate();
        if (window is null || !window.IsVisible || window.IsMinimized)
        {
            return "找不到可见的微信窗口。";
        }

        CapturedFrame frame;
        try
        {
            frame = _capture.Capture(window);
        }
        catch (WindowCaptureUnavailableException exception)
        {
            return $"抓帧失败：{exception.Message}";
        }

        var chatRegion = _chatRegionLocator.Locate(frame);
        var bubbles = _detector.Detect(frame, chatRegion.Bounds);

        CapturePixelRect? anchor = null;
        foreach (var bubble in bubbles)
        {
            if (bubble.Side == MessageSide.Remote)
            {
                anchor = bubble.Bounds;
            }
        }

        if (anchor is not { } target)
        {
            return $"这一帧没有对方的气泡（共检测到 {bubbles.Count} 个）。";
        }

        _overlay.Show(
            new OverlayContent(
                "（演示锚点，未调用 Jev）",
                "要认真回，已经有情绪了",
                "直接回答  67%",
                new[]
                {
                    new OverlayRow("话里有话", 0.87),
                    new OverlayRow("要一个具体答案", 0.56),
                }),
            target,
            chatRegion.Bounds);

        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(this, EventArgs.Empty);

        return $"演示 HUD 已挂到 {target}，跟随中。移动或缩放微信看它是否跟上。";
    }

    private void OnTick(object? sender, EventArgs e) => _overlay.Follow(_tracker.Locate());

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _overlay.Dispose();
    }
}
