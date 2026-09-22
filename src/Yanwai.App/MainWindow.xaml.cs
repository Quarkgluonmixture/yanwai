using System.IO;
using System.Windows;
using System.Windows.Threading;
using Yanwai.Capture;
using Yanwai.Core.Geometry;
using Yanwai.Core.Windows;
using Yanwai.Windows;

namespace Yanwai.App;

public partial class MainWindow : Window
{
    private readonly IWeChatWindowTracker _tracker = new Win32WeChatWindowTracker();
    private readonly IWindowCapture _capture = new Win32ScreenRegionCapture();
    private readonly DispatcherTimer _refreshTimer;
    private WeChatWindowSnapshot? _snapshot;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, OnRefreshTimer, Dispatcher);
        Loaded += (_, _) => RefreshSnapshot();
        Closed += (_, _) => _refreshTimer.Stop();
        StatusText.Text = "Capture is manual. No frame is saved until you press the button.";
    }

    private void OnRefreshTimer(object? sender, EventArgs eventArgs) => RefreshSnapshot();

    private void RefreshClick(object sender, RoutedEventArgs eventArgs) => RefreshSnapshot();

    private async void CaptureClick(object sender, RoutedEventArgs eventArgs)
    {
        RefreshSnapshot();
        if (_snapshot is null)
        {
            StatusText.Text = "WeChat was not found. Start and restore Windows WeChat, then retry.";
            return;
        }

        if (_snapshot.IsMinimized || !_snapshot.IsVisible)
        {
            StatusText.Text = "Capture is suspended while WeChat is minimized or hidden.";
            return;
        }

        try
        {
            Hide();
            await Task.Delay(150);
            var current = _tracker.Locate() ?? throw new WindowCaptureUnavailableException("WeChat disappeared before capture.");
            var frame = _capture.Capture(current);
            var directory = Path.Combine(Environment.CurrentDirectory, "debug-captures");
            var path = Path.Combine(directory, $"wechat-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            PngFrameWriter.Save(frame, path);
            CapturePreview.Source = PngFrameWriter.ToBitmapSource(frame);
            StatusText.Text = $"Saved {frame.Width}×{frame.Height} frame via {frame.Method} in {frame.Duration.TotalMilliseconds:F1} ms: {Path.GetFullPath(path)}";
        }
        catch (Exception exception) when (exception is WindowCaptureUnavailableException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Capture failed: {exception.Message}";
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void RefreshSnapshot()
    {
        _snapshot = _tracker.Locate();
        if (_snapshot is null)
        {
            WindowDetails.Text = "WeChat window not found.";
            return;
        }

        var snapshot = _snapshot;
        WindowDetails.Text = string.Join(Environment.NewLine,
            $"HWND: 0x{snapshot.Handle:X}",
            $"Process: {snapshot.ProcessName} (PID {snapshot.ProcessId})",
            $"Title: {snapshot.Title}",
            $"Class: {snapshot.ClassName}",
            $"Visible / minimized / foreground: {snapshot.IsVisible} / {snapshot.IsMinimized} / {snapshot.IsForeground}",
            $"Top-level: {Format(snapshot.TopLevelBounds)}",
            $"Client: {Format(snapshot.ClientBounds)}",
            $"Render: {(snapshot.RenderBounds is { } render ? Format(render) : "not found; capture uses client")}",
            $"Capture: {Format(snapshot.CaptureBounds)}",
            $"Monitor: {snapshot.Monitor.DeviceName} (primary={snapshot.Monitor.IsPrimary})",
            $"Monitor bounds: {Format(snapshot.Monitor.Bounds)}",
            $"DPI: {snapshot.Dpi.X}×{snapshot.Dpi.Y} ({snapshot.Dpi.ScaleX:P0})");

        if (snapshot.IsMinimized)
        {
            StatusText.Text = "WeChat is minimized; tracking continues and capture is suspended.";
        }
    }

    private static string Format(DesktopPixelRect rect) =>
        $"x={rect.X}, y={rect.Y}, width={rect.Width}, height={rect.Height}";
}
