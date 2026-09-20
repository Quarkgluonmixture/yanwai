using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Windows;

var tracker = new Win32WeChatWindowTracker();
var snapshot = tracker.Locate();
if (snapshot is null)
{
    Console.Error.WriteLine("WeChat window not found. Start and restore Windows WeChat, then retry.");
    return 2;
}

Console.WriteLine($"HWND: 0x{snapshot.Handle:X}");
Console.WriteLine($"Process: {snapshot.ProcessName} (PID {snapshot.ProcessId})");
Console.WriteLine($"Title: {snapshot.Title}");
Console.WriteLine($"Class: {snapshot.ClassName}");
Console.WriteLine($"Visible: {snapshot.IsVisible}");
Console.WriteLine($"Minimized: {snapshot.IsMinimized}");
Console.WriteLine($"Foreground: {snapshot.IsForeground}");
Console.WriteLine($"Top-level bounds: {Format(snapshot.TopLevelBounds)}");
Console.WriteLine($"Client bounds: {Format(snapshot.ClientBounds)}");
Console.WriteLine($"Render bounds: {(snapshot.RenderBounds is { } render ? Format(render) : "not found; using client bounds")}");
Console.WriteLine($"Capture bounds: {Format(snapshot.CaptureBounds)}");
Console.WriteLine($"Monitor: {snapshot.Monitor.DeviceName} (primary={snapshot.Monitor.IsPrimary})");
Console.WriteLine($"Monitor bounds: {Format(snapshot.Monitor.Bounds)}");
Console.WriteLine($"Monitor work area: {Format(snapshot.Monitor.WorkArea)}");
Console.WriteLine($"DPI: {snapshot.Dpi.X} x {snapshot.Dpi.Y} ({snapshot.Dpi.ScaleX:P0} scale)");

var captureIndex = Array.FindIndex(args, argument => argument.Equals("--capture", StringComparison.OrdinalIgnoreCase));
if (captureIndex < 0)
{
    return 0;
}

if (snapshot.IsMinimized || !snapshot.IsVisible)
{
    Console.Error.WriteLine("Capture skipped because WeChat is minimized or hidden.");
    return 3;
}

var requestedPath = captureIndex + 1 < args.Length ? args[captureIndex + 1] : null;
var capturePath = string.IsNullOrWhiteSpace(requestedPath)
    ? Path.Combine(Environment.CurrentDirectory, "debug-captures", $"wechat-{DateTime.Now:yyyyMMdd-HHmmss}.png")
    : requestedPath;

try
{
    var frame = new Win32ScreenRegionCapture().Capture(snapshot);
    PngFrameWriter.Save(frame, capturePath);
    Console.WriteLine($"Captured frame: {Path.GetFullPath(capturePath)}");
    Console.WriteLine($"Frame: {frame.Width}x{frame.Height}, method={frame.Method}, capture_ms={frame.Duration.TotalMilliseconds:F1}");
    return 0;
}
catch (WindowCaptureUnavailableException exception)
{
    Console.Error.WriteLine($"Capture failed: {exception.Message}");
    return 3;
}

static string Format(DesktopPixelRect rect) =>
    $"x={rect.X}, y={rect.Y}, width={rect.Width}, height={rect.Height}";
