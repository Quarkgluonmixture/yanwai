using System.Text;
using System.Text.Json;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Ocr;
using WeChatJevHud.Vision;
using WeChatJevHud.Windows;

var ocrEvaluationIndex = FindOption(args, "--ocr-evaluate");
if (ocrEvaluationIndex >= 0)
{
    if (ocrEvaluationIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--ocr-evaluate requires a JSON manifest path.");
        return 64;
    }

    try
    {
        var tessdata = OptionValue(args, "--tessdata")
            ?? Path.Combine(Environment.CurrentDirectory, ".ocr-cache", "tessdata");
        return await EvaluateOcrAsync(
            args[ocrEvaluationIndex + 1],
            tessdata,
            OptionValue(args, "--ocr-output"));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
    {
        Console.Error.WriteLine($"OCR evaluation failed: {exception.Message}");
        return 5;
    }
}

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

    Console.WriteLine($"Chat ROI: {FormatCapture(result.ChatRegion.Bounds)}, detection_score={result.ChatRegion.DetectionScore:F3}");
    Console.WriteLine("side, x, y, width, height, detection_score");
    foreach (var bubble in result.Bubbles)
    {
        Console.WriteLine(
            $"{bubble.Side}, {bubble.Bounds.X}, {bubble.Bounds.Y}, {bubble.Bounds.Width}, {bubble.Bounds.Height}, {bubble.DetectionScore:F3}");
    }

    Console.WriteLine($"bubble_detect_ms={result.Duration.TotalMilliseconds:F1}");
    Console.WriteLine($"Debug visualization: {Path.GetFullPath(outputPath)}");
    return 0;
}

static async Task<int> EvaluateOcrAsync(
    string manifestPath,
    string tessdataPath,
    string? outputPath)
{
    var fullManifestPath = Path.GetFullPath(manifestPath);
    var manifest = JsonSerializer.Deserialize<OcrEvaluationManifest>(
        await File.ReadAllTextAsync(fullManifestPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("OCR evaluation manifest is empty.");
    if (manifest.Fixtures.Count == 0)
    {
        throw new InvalidOperationException("OCR evaluation manifest has no fixtures.");
    }

    var manifestDirectory = Path.GetDirectoryName(fullManifestPath)!;
    var frames = new Dictionary<string, CapturedFrame>(StringComparer.OrdinalIgnoreCase);
    var fixtures = new List<OcrEvaluationFixture>(manifest.Fixtures.Count);
    foreach (var item in manifest.Fixtures)
    {
        var imagePath = Path.GetFullPath(item.Image, manifestDirectory);
        if (!frames.TryGetValue(imagePath, out var frame))
        {
            frame = PngFrameReader.Load(imagePath);
            frames.Add(imagePath, frame);
        }

        fixtures.Add(new OcrEvaluationFixture(
            item.Name,
            item.Expected,
            new ImageCrop(frame, new CapturePixelRect(item.X, item.Y, item.Width, item.Height))));
    }

    var evaluator = new OcrEvaluator();
    using var tesseractRaw = new TesseractOcrEngine(Path.GetFullPath(tessdataPath));
    using var tesseractUpscaled = new TesseractOcrEngine(
        Path.GetFullPath(tessdataPath),
        preparation: OcrImagePreparation.Upscaled);
    var windowsRaw = new WindowsMediaOcrEngine("zh-Hans-CN");
    var windowsUpscaled = new WindowsMediaOcrEngine("zh-Hans-CN", OcrImagePreparation.Upscaled);
    var adaptive = new AdaptiveOcrEngine(
        [tesseractRaw, tesseractUpscaled],
        windowsUpscaled,
        confidenceThreshold: 0.75);
    IOcrEngine[] engines =
    [
        windowsRaw,
        windowsUpscaled,
        tesseractRaw,
        tesseractUpscaled,
        adaptive,
    ];

    var report = new StringBuilder();
    foreach (var engine in engines)
    {
        WriteLine($"## engine: {engine.Name}");
        WriteLine("| fixture | expected | recognized | ocr_confidence | elapsed_ms |");
        WriteLine("| --- | --- | --- | ---: | ---: |");
        var rows = await evaluator.EvaluateAsync(engine, fixtures, CancellationToken.None);
        foreach (var row in rows)
        {
            var confidence = row.OcrConfidence is { } value ? value.ToString("F3") : "n/a";
            WriteLine(
                $"| {TableCell(row.Fixture)} | {TableCell(row.Expected)} | {TableCell(row.Recognized)} | {confidence} | {row.Elapsed.TotalMilliseconds:F1} |");
        }

        var statusNotes = rows.Where(row => row.Status != OcrTextStatus.Recognized).ToArray();
        if (statusNotes.Length > 0)
        {
            WriteLine();
            WriteLine("Status notes:");
            foreach (var row in statusNotes)
            {
                WriteLine($"- {TableCell(row.Fixture)}: {row.Status}");
            }
        }

        WriteLine();
    }

    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await File.WriteAllTextAsync(fullOutputPath, report.ToString());
        Console.WriteLine($"OCR evaluation artifact: {fullOutputPath}");

        var cropDirectory = Path.Combine(
            Path.GetDirectoryName(fullOutputPath)!,
            $"{Path.GetFileNameWithoutExtension(fullOutputPath)}-crops");
        Directory.CreateDirectory(cropDirectory);
        foreach (var fixture in fixtures)
        {
            var cropPath = Path.Combine(cropDirectory, $"{SafeFileName(fixture.Name)}.png");
            PngFrameWriter.Save(ImageCropExtractor.Extract(fixture.Crop), cropPath);
        }

        Console.WriteLine($"OCR crop artifacts: {cropDirectory}");
    }

    return 0;

    void WriteLine(string value = "")
    {
        Console.WriteLine(value);
        report.AppendLine(value);
    }
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

static string TableCell(string value) =>
    value.Replace('|', '¦').ReplaceLineEndings(" ");

static string SafeFileName(string value)
{
    var invalid = Path.GetInvalidFileNameChars();
    return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
}

internal sealed record OcrEvaluationManifest(List<OcrEvaluationManifestItem> Fixtures);

internal sealed record OcrEvaluationManifestItem(
    string Name,
    string Image,
    string Expected,
    int X,
    int Y,
    int Width,
    int Height);
