using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Ocr;
using WeChatJevHud.Observer;
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

if (FindOption(args, "--observe") >= 0)
{
    try
    {
        return await ObserveWeChatAsync(args);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Observer failed: {exception.Message}");
        return 6;
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

    const double trustedTesseractThreshold = 0.90;
    var evaluator = new OcrEvaluator();
    using var tesseractRaw = new TesseractOcrEngine(
        Path.GetFullPath(tessdataPath),
        lowConfidenceThreshold: trustedTesseractThreshold);
    using var tesseractUpscaled = new TesseractOcrEngine(
        Path.GetFullPath(tessdataPath),
        lowConfidenceThreshold: trustedTesseractThreshold,
        preparation: OcrImagePreparation.Upscaled);
    var windowsRaw = new WindowsMediaOcrEngine("zh-Hans-CN");
    var windowsUpscaled = new WindowsMediaOcrEngine("zh-Hans-CN", OcrImagePreparation.Upscaled);
    var adaptive = new AdaptiveOcrEngine(
        [tesseractRaw, tesseractUpscaled],
        windowsUpscaled,
        confidenceThreshold: trustedTesseractThreshold);
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
        WriteLine("| fixture | expected | raw_recognized | normalized_recognized | status | ocr_confidence | raw_exact_match | normalized_match | raw_cer | normalized_cer | elapsed_ms |");
        WriteLine("| --- | --- | --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: |");
        var rows = await evaluator.EvaluateAsync(engine, fixtures, CancellationToken.None);
        foreach (var row in rows)
        {
            var confidence = row.OcrConfidence is { } value ? value.ToString("F3") : "n/a";
            WriteLine(
                $"| {TableCell(row.Fixture)} | {TableCell(row.Expected)} | {TableCell(row.RawRecognized)} | {TableCell(row.NormalizedRecognized)} | {row.Status} | {confidence} | {row.RawExactMatch.ToString().ToLowerInvariant()} | {row.NormalizedMatch.ToString().ToLowerInvariant()} | {row.RawCharacterErrorRate:F3} | {row.NormalizedCharacterErrorRate:F3} | {row.Elapsed.TotalMilliseconds:F1} |");
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

static async Task<int> ObserveWeChatAsync(string[] arguments)
{
    var intervalMilliseconds = PositiveIntOption(arguments, "--interval-ms", 200)!.Value;
    var durationSeconds = PositiveIntOption(arguments, "--observe-seconds", null);
    var debugText = FindOption(arguments, "--debug-text") >= 0;
    var tessdata = Path.GetFullPath(
        OptionValue(arguments, "--tessdata")
        ?? Path.Combine(Environment.CurrentDirectory, ".ocr-cache", "tessdata"));
    if (!Directory.Exists(tessdata))
    {
        throw new InvalidOperationException(
            $"Tesseract data was not found at {tessdata}. Complete the Phase 3 OCR setup or pass --tessdata.");
    }

    using var tesseractRaw = new TesseractOcrEngine(tessdata, lowConfidenceThreshold: 0.90);
    using var tesseractUpscaled = new TesseractOcrEngine(
        tessdata,
        lowConfidenceThreshold: 0.90,
        preparation: OcrImagePreparation.Upscaled);
    var adaptive = new AdaptiveOcrEngine(
        [tesseractRaw, tesseractUpscaled],
        new WindowsMediaOcrEngine("zh-Hans-CN", OcrImagePreparation.Upscaled),
        confidenceThreshold: 0.90);
    IMessageObserver observer = new MessageObserver(
        new DarkThemeChatRegionLocator(),
        new DarkThemeBubbleDetector(),
        adaptive,
        new ChatRoiChangeDetector(),
        new VisualConversationIdentityProvider());

    observer.ConversationChanged += (_, eventArgs) =>
    {
        Console.WriteLine(eventArgs.PreviousEpoch is null
            ? $"[epoch {eventArgs.CurrentEpoch.Id}] observation started"
            : $"conversation switch confirmed epoch {eventArgs.PreviousEpoch.Id} -> {eventArgs.CurrentEpoch.Id}");
    };
    observer.MessageObserved += (_, eventArgs) =>
    {
        if (eventArgs.Message.Origin != MessageObservationKind.LiveNew)
        {
            Console.WriteLine(
                $"[epoch {eventArgs.Message.ConversationEpochId}] {eventArgs.Message.Origin.ToString().ToLowerInvariant()} " +
                $"{eventArgs.Message.Side} {DiagnosticText(eventArgs.Message, debugText)} " +
                $"status={eventArgs.Message.OcrStatus} semantic_ready={eventArgs.Message.IsTrustedForSemantics.ToString().ToLowerInvariant()}");
        }
    };
    observer.NewMessageObserved += (_, eventArgs) =>
    {
        Console.WriteLine(
            $"[epoch {eventArgs.Message.ConversationEpochId}] NEW {eventArgs.Message.Side} " +
            $"{DiagnosticText(eventArgs.Message, debugText)} id={eventArgs.Message.Id} " +
            $"status={eventArgs.Message.OcrStatus} semantic_ready={eventArgs.Message.IsTrustedForSemantics.ToString().ToLowerInvariant()}");
    };

    using var cancellation = new CancellationTokenSource();
    if (durationSeconds is { } seconds)
    {
        cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));
    }

    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    var tracker = new Win32WeChatWindowTracker();
    var capture = new Win32ScreenRegionCapture();
    using var process = Process.GetCurrentProcess();
    var stableWindow = Stopwatch.StartNew();
    var stableWindowCpuStart = process.TotalProcessorTime;
    long stableWindowUnchangedFrames = 0;
    var wasUnavailable = false;
    Console.WriteLine(
        $"Observer running every {intervalMilliseconds} ms. Text output is " +
        $"{(debugText ? "enabled and truncated" : "redacted")}. Press Ctrl+C to stop.");

    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            var window = tracker.Locate();
            if (window is null || !window.IsVisible || window.IsMinimized)
            {
                if (!wasUnavailable)
                {
                    Console.WriteLine("capture suspended: WeChat is unavailable, hidden, or minimized");
                    wasUnavailable = true;
                }
            }
            else
            {
                if (wasUnavailable)
                {
                    Console.WriteLine("capture resumed");
                    wasUnavailable = false;
                }

                try
                {
                    var frame = capture.Capture(window);
                    var result = await observer.ObserveAsync(frame, cancellation.Token);
                    PrintIdentityObservation(result.Identity);
                    foreach (var id in result.DuplicateMessageIds)
                    {
                        Console.WriteLine($"[epoch {result.Epoch.Id}] duplicate suppressed id={id}");
                    }

                    if (result.FrameChanged)
                    {
                        Console.WriteLine(
                            $"timing capture_ms={frame.Duration.TotalMilliseconds:F1} " +
                            $"frame_check_ms={result.Timings.FrameCheck.TotalMilliseconds:F1} " +
                            $"change_detect_ms={result.Timings.ChangeDetect.TotalMilliseconds:F1} " +
                            $"bubble_detect_ms={result.Timings.BubbleDetect.TotalMilliseconds:F1} " +
                            $"ocr_ms={result.Timings.Ocr.TotalMilliseconds:F1} " +
                            $"observer_reconcile_ms={result.Timings.ObserverReconcile.TotalMilliseconds:F1}");
                        stableWindow.Restart();
                        stableWindowCpuStart = process.TotalProcessorTime;
                        stableWindowUnchangedFrames = 0;
                    }
                    else
                    {
                        stableWindowUnchangedFrames++;
                        if (stableWindowUnchangedFrames % 25 == 0)
                        {
                            Console.WriteLine(
                                $"idle timing capture_ms={frame.Duration.TotalMilliseconds:F1} " +
                                $"frame_check_ms={result.Timings.FrameCheck.TotalMilliseconds:F1} " +
                                $"change_detect_ms={result.Timings.ChangeDetect.TotalMilliseconds:F1}");
                        }
                    }
                }
                catch (WindowCaptureUnavailableException exception)
                {
                    if (!wasUnavailable)
                    {
                        Console.WriteLine($"capture suspended: {exception.Message}");
                        wasUnavailable = true;
                    }
                }
            }

            await Task.Delay(intervalMilliseconds, cancellation.Token);
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }

    stableWindow.Stop();
    var stableCpu = process.TotalProcessorTime - stableWindowCpuStart;
    var stableCpuPercent = stableWindow.Elapsed > TimeSpan.Zero
        ? stableCpu.TotalMilliseconds /
          (stableWindow.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100
        : 0;
    PrintObserverCounters(observer.Counters);
    Console.WriteLine($"idle_window_frames={stableWindowUnchangedFrames}");
    Console.WriteLine($"idle_window_seconds={stableWindow.Elapsed.TotalSeconds:F2}");
    Console.WriteLine($"idle_process_cpu_percent={stableCpuPercent:F2}");
    return 0;
}

static string DiagnosticText(ObservedMessage message, bool debugText)
{
    if (string.IsNullOrEmpty(message.NormalizedText))
    {
        return "text=<empty>";
    }

    if (!debugText)
    {
        return $"text=<redacted chars={message.NormalizedText.Length}>";
    }

    const int limit = 60;
    var normalized = message.NormalizedText.ReplaceLineEndings(" ");
    var truncated = normalized.Length <= limit ? normalized : $"{normalized[..limit]}…";
    return $"text=\"{truncated.Replace("\"", "'", StringComparison.Ordinal)}\"";
}

static void PrintObserverCounters(ObserverCounters counters)
{
    Console.WriteLine("observer counters:");
    Console.WriteLine($"frames_checked={counters.FramesChecked}");
    Console.WriteLine($"unchanged_frames={counters.UnchangedFrames}");
    Console.WriteLine($"changed_frames={counters.ChangedFrames}");
    Console.WriteLine($"bubble_detection_runs={counters.BubbleDetectionRuns}");
    Console.WriteLine($"ocr_calls={counters.OcrCalls}");
    Console.WriteLine($"messages_emitted={counters.MessagesEmitted}");
    Console.WriteLine($"new_messages={counters.MessagesEmitted}");
    Console.WriteLine($"duplicates_suppressed={counters.DuplicatesSuppressed}");
    Console.WriteLine($"conversation_switches={counters.ConversationSwitches}");
    Console.WriteLine($"identity_mismatch_candidates={counters.IdentityMismatchCandidates}");
    Console.WriteLine($"identity_rebases={counters.IdentityRebases}");
    Console.WriteLine($"identity_switches_confirmed={counters.IdentitySwitchesConfirmed}");
    Console.WriteLine($"identity_switches_suppressed={counters.IdentitySwitchesSuppressed}");
    Console.WriteLine($"layout_transitions={counters.LayoutTransitions}");
}

static void PrintIdentityObservation(ConversationIdentityObservation identity)
{
    if (!identity.CandidateChanged)
    {
        return;
    }

    var overlap = $"message_overlap={identity.MessageOverlap}/{identity.VisibleCandidates}";
    var evidence =
        $"identity_evidence=\"{identity.ProviderDiagnostics}\" " +
        $"{overlap} live_tail_match={identity.LiveTailMatched.ToString().ToLowerInvariant()}";
    switch (identity.Decision)
    {
        case ConversationIdentityDecision.RebaseSameConversation:
            Console.WriteLine($"identity candidate changed {evidence} decision=REBASE_SAME_CONVERSATION");
            break;
        case ConversationIdentityDecision.LayoutTransition:
            Console.WriteLine($"identity candidate changed {evidence} decision=LAYOUT_TRANSITION_SUPPRESSED");
            break;
        case ConversationIdentityDecision.PendingSwitch:
            Console.WriteLine(
                $"identity candidate changed {evidence} " +
                $"pending_switch={identity.PendingObservations}/{identity.RequiredObservations}");
            break;
        case ConversationIdentityDecision.ConfirmedSwitch:
            Console.WriteLine($"identity candidate changed {evidence} decision=CONFIRMED_SWITCH");
            break;
    }
}

static int? PositiveIntOption(string[] arguments, string option, int? defaultValue)
{
    var raw = OptionValue(arguments, option);
    if (raw is null)
    {
        return defaultValue;
    }

    if (!int.TryParse(raw, out var parsed) || parsed <= 0)
    {
        throw new ArgumentException($"{option} must be a positive integer.");
    }

    return parsed;
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
    value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace('|', '¦');

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
