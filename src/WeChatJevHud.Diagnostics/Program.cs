using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Vision;
using WeChatJevHud.Windows;

var detectIndex = FindOption(args, "--detect");
if (detectIndex >= 0)
{
    if (detectIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--detect requires an input PNG path.");
        return 64;
    }

    try
    {
        var inputPath = args[detectIndex + 1];
        var frame = PngFrameReader.Load(inputPath);
        var outputPath = OptionValue(args, "--output") ?? DefaultDebugPath(inputPath);
        return AnalyzeAndWrite(frame, outputPath);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Detection failed: {exception.Message}");
        return 4;
    }
}

var tracker = new Win32WeChatWindowTracker();
var snapshot = tracker.Locate();
if (snapshot is null)
{
    Console.Error.WriteLine("WeChat window not found. Start and restore Windows WeChat, then retry.");
    return 2;
}

Console.WriteLine($"HWND: 0x{snapshot.Handle:X}");
Console.WriteLine($"Process: {snapshot.ProcessName} (PID {snapshot.ProcessId})");
Console.WriteLine($"Title: {snapshot.Title} (diagnostic only; not conversation identity)");
Console.WriteLine($"Class: {snapshot.ClassName}");
Console.WriteLine($"Visible: {snapshot.IsVisible}");
Console.WriteLine($"Minimized: {snapshot.IsMinimized}");
Console.WriteLine($"Foreground: {snapshot.IsForeground}");
Console.WriteLine($"Top-level bounds: {FormatDesktop(snapshot.TopLevelBounds)}");
Console.WriteLine($"Client bounds: {FormatDesktop(snapshot.ClientBounds)}");
Console.WriteLine($"Render bounds: {(snapshot.RenderBounds is { } render ? FormatDesktop(render) : "not found; using client bounds")}");
Console.WriteLine($"Capture bounds: {FormatDesktop(snapshot.CaptureBounds)}");
Console.WriteLine($"Monitor: {snapshot.Monitor.DeviceName} (primary={snapshot.Monitor.IsPrimary})");
Console.WriteLine($"Monitor bounds: {FormatDesktop(snapshot.Monitor.Bounds)}");
Console.WriteLine($"Monitor work area: {FormatDesktop(snapshot.Monitor.WorkArea)}");
Console.WriteLine($"DPI: {snapshot.Dpi.X} x {snapshot.Dpi.Y} ({snapshot.Dpi.ScaleX:P0} scale)");

var captureIndex = FindOption(args, "--capture");
var captureAndDetect = FindOption(args, "--capture-detect") >= 0;
if (captureIndex < 0 && !captureAndDetect)
{
    return 0;
}

if (snapshot.IsMinimized || !snapshot.IsVisible)
{
    Console.Error.WriteLine("Capture skipped because WeChat is minimized or hidden.");
    return 3;
}

var requestedPath = captureIndex >= 0 && captureIndex + 1 < args.Length && !args[captureIndex + 1].StartsWith("--", StringComparison.Ordinal)
    ? args[captureIndex + 1]
    : null;
var capturePath = string.IsNullOrWhiteSpace(requestedPath)
    ? Path.Combine(Environment.CurrentDirectory, "debug-captures", $"wechat-{DateTime.Now:yyyyMMdd-HHmmss}.png")
    : requestedPath;

try
{
    var frame = new Win32ScreenRegionCapture().Capture(snapshot);
    PngFrameWriter.Save(frame, capturePath);
    Console.WriteLine($"Captured frame: {Path.GetFullPath(capturePath)}");
    Console.WriteLine($"Frame: {frame.Width}x{frame.Height}, method={frame.Method}, capture_ms={frame.Duration.TotalMilliseconds:F1}");
    if (captureAndDetect)
    {
        return AnalyzeAndWrite(frame, OptionValue(args, "--output") ?? DefaultDebugPath(capturePath));
    }

    return 0;
}
catch (WindowCaptureUnavailableException exception)
{
    Console.Error.WriteLine($"Capture failed: {exception.Message}");
    return 3;
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine($"Detection failed: {exception.Message}");
    return 4;
}

static int AnalyzeAndWrite(CapturedFrame frame, string outputPath)
{
    var pipeline = new BubbleDetectionPipeline(
        new DarkThemeChatRegionLocator(),
        new DarkThemeBubbleDetector());
    var result = pipeline.Analyze(frame);
    var debugFrame = DetectionDebugRenderer.Render(frame, result);
    PngFrameWriter.Save(debugFrame, outputPath);

    Console.WriteLine($"Chat ROI: {FormatCapture(result.ChatRegion.Bounds)}, confidence={result.ChatRegion.Confidence:F3}");
    Console.WriteLine("side, x, y, width, height, confidence");
    foreach (var bubble in result.Bubbles)
    {
        Console.WriteLine(
            $"{bubble.Side}, {bubble.Bounds.X}, {bubble.Bounds.Y}, {bubble.Bounds.Width}, {bubble.Bounds.Height}, {bubble.Confidence:F3}");
    }

    Console.WriteLine($"bubble_detect_ms={result.Duration.TotalMilliseconds:F1}");
    Console.WriteLine($"Debug visualization: {Path.GetFullPath(outputPath)}");
    return 0;
}

static int FindOption(string[] arguments, string option) =>
    Array.FindIndex(arguments, argument => argument.Equals(option, StringComparison.OrdinalIgnoreCase));

static string? OptionValue(string[] arguments, string option)
{
    var index = FindOption(arguments, option);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static string DefaultDebugPath(string inputPath)
{
    var fullPath = Path.GetFullPath(inputPath);
    return Path.Combine(
        Path.GetDirectoryName(fullPath)!,
        $"{Path.GetFileNameWithoutExtension(fullPath)}-bubbles.png");
}

static string FormatDesktop(DesktopPixelRect rect) =>
    $"x={rect.X}, y={rect.Y}, width={rect.Width}, height={rect.Height}";

static string FormatCapture(CapturePixelRect rect) =>
    $"x={rect.X}, y={rect.Y}, width={rect.Width}, height={rect.Height}";
