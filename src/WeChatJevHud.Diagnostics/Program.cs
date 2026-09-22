using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Ocr;
using WeChatJevHud.Observer;
using WeChatJevHud.Vision;
using WeChatJevHud.Windows;

var identityAudit = OptionValue(args, "--identity-audit");
if (identityAudit is not null)
{
    var other = OptionValue(args, "--compare-image") ?? throw new ArgumentException("--compare-image is required.");
    var output = Path.GetFullPath(OptionValue(args, "--output") ?? ".ocr-cache/header-identity-audit.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var frames = new[] { PngFrameReader.Load(identityAudit), PngFrameReader.Load(other) };
    var locator = new DarkThemeChatRegionLocator();
    var provider = new VisualConversationIdentityProvider();
    var regions = frames.Select(frame => locator.Locate(frame).Bounds).ToArray();
    var titles = frames.Select((frame, index) => provider.LocateTitleRegion(frame, regions[index])).ToArray();
    for (var i = 0; i < frames.Length; i++)
        if (titles[i] is { } title)
            await File.WriteAllBytesAsync(Path.ChangeExtension(output, $".{i}-title.png"),
                ImageCropPngEncoder.Encode(new ImageCrop(frames[i], title)));
    var comparison = provider.Compare(provider.GetVisualEvidence(frames[0], regions[0]), provider.GetVisualEvidence(frames[1], regions[1]));
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { regions, titles, comparison }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Header audit: {output}; same={comparison.IsMatch}; title_visual_distance={comparison.TitleVisualDistance:F4}; title_aspect_distance={comparison.TitleAspectDistance:F4}");
    return 0;
}

var routingManifest = OptionValue(args, "--routing-audit");
if (routingManifest is not null)
{
    var manifestPath = Path.GetFullPath(routingManifest);
    var manifest = JsonSerializer.Deserialize<OcrEvaluationManifest>(
        await File.ReadAllTextAsync(manifestPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var rows = manifest.Fixtures.Select(item =>
    {
        var frame = PngFrameReader.Load(Path.Combine(Path.GetDirectoryName(manifestPath)!, item.Crop!));
        var crop = new ImageCrop(frame, new CapturePixelRect(0, 0, frame.Width, frame.Height),
            string.Equals(item.Role, "QuotedText", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Role, "quoted", StringComparison.OrdinalIgnoreCase)
                ? OcrCropRole.QuotedText : OcrCropRole.MainMessage);
        var analysis = new ScaleAwareOcrRoutingPolicy().Analyze(crop);
        return new
        {
            item.Name,
            item.Expected,
            item.CaptureDpi,
            item.Crop,
            BubbleWidth = frame.Width,
            BubbleHeight = frame.Height,
            RecordedRoute = item.OcrRoute,
            Analysis = analysis
        };
    }).ToArray();
    var output = OptionValue(args, "--output") ?? Path.ChangeExtension(manifestPath, ".routing.json");
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(rows,
        new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
    Console.WriteLine($"Routing audit: {rows.Length} crops -> {output}");
    return 0;
}

var productionEvaluationIndex = FindOption(args, "--production-ocr-evaluate");
if (productionEvaluationIndex >= 0)
{
    if (productionEvaluationIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--production-ocr-evaluate requires a JSON manifest path.");
        return 64;
    }

    try
    {
        return await EvaluateProductionOcrAsync(
            args[productionEvaluationIndex + 1],
            OptionValue(args, "--tessdata")
                ?? Path.Combine(Environment.CurrentDirectory, ".ocr-cache", "tessdata"),
            OptionValue(args, "--ocr-output")
                ?? Path.Combine(Environment.CurrentDirectory, ".ocr-cache", "phase4.5-production-ocr.md"),
            OptionValue(args, "--paddle-python"),
            OptionValue(args, "--paddle-device") ?? "gpu:0",
            FindOption(args, "--paddle-worker-debug") >= 0);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
    {
        Console.Error.WriteLine($"Production OCR evaluation failed: {exception.Message}");
        return 7;
    }
}

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

var collectCalibrationIndex = FindOption(args, "--collect-ocr-calibration");
if (collectCalibrationIndex >= 0)
{
    if (collectCalibrationIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--collect-ocr-calibration requires an expected-text file.");
        return 64;
    }

    try
    {
        return await CollectOcrCalibrationAsync(
            args[collectCalibrationIndex + 1],
            OptionValue(args, "--calibration-dir")
                ?? Path.Combine(Environment.CurrentDirectory, ".ocr-cache", "phase4.5-calibration"),
            OptionValue(args, "--calibration-side"),
            NonNegativeIntOption(args, "--calibration-skip", 0));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
    {
        Console.Error.WriteLine($"Calibration collection failed: {exception.Message}");
        return 9;
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
        var imagePath = Path.GetFullPath(
            item.Image ?? item.Crop ?? throw new InvalidOperationException($"Fixture '{item.Name}' has no image or crop."),
            manifestDirectory);
        if (!frames.TryGetValue(imagePath, out var frame))
        {
            frame = PngFrameReader.Load(imagePath);
            frames.Add(imagePath, frame);
        }

        var bounds = item.Crop is not null
            ? new CapturePixelRect(0, 0, frame.Width, frame.Height)
            : new CapturePixelRect(item.X, item.Y, item.Width, item.Height);
        fixtures.Add(new OcrEvaluationFixture(
            item.Name,
            item.Expected,
            new ImageCrop(frame, bounds, ParseCropRole(item.Role))));
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

static async Task<int> EvaluateProductionOcrAsync(
    string manifestPath,
    string tessdataPath,
    string outputPath,
    string? configuredPython,
    string paddleDevice,
    bool workerDebug)
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
        var imagePath = Path.GetFullPath(
            item.Image ?? item.Crop ?? throw new InvalidOperationException($"Fixture '{item.Name}' has no image or crop."),
            manifestDirectory);
        if (!frames.TryGetValue(imagePath, out var frame))
        {
            frame = PngFrameReader.Load(imagePath);
            frames.Add(imagePath, frame);
        }

        var bounds = item.Crop is not null
            ? new CapturePixelRect(0, 0, frame.Width, frame.Height)
            : new CapturePixelRect(item.X, item.Y, item.Width, item.Height);
        fixtures.Add(new OcrEvaluationFixture(
            item.Name,
            item.Expected,
            new ImageCrop(frame, bounds, ParseCropRole(item.Role))));
    }

    const double trustedTesseractThreshold = 0.90;
    using var tesseractRaw = new TesseractOcrEngine(
        Path.GetFullPath(tessdataPath),
        lowConfidenceThreshold: trustedTesseractThreshold);
    using var tesseractUpscaled = new TesseractOcrEngine(
        Path.GetFullPath(tessdataPath),
        lowConfidenceThreshold: trustedTesseractThreshold,
        preparation: OcrImagePreparation.Upscaled);
    var adaptive = new AdaptiveOcrEngine(
        [tesseractRaw, tesseractUpscaled],
        new WindowsMediaOcrEngine("zh-Hans-CN", OcrImagePreparation.Upscaled),
        trustedTesseractThreshold);
    var counters = new ProductionOcrCounters();
    var python = configuredPython
        ?? Environment.GetEnvironmentVariable("WECHAT_JEV_PADDLE_PYTHON")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatJevHud", "paddle-ocr", ".venv", "Scripts", "python.exe");
    await using var worker = new PaddleWorkerClient(
        PaddleWorkerOptions.Create(
            python,
            [
                Path.Combine(Environment.CurrentDirectory, "scripts", "paddle_ocr_worker.py"),
                "--model", "PP-OCRv6_small_rec",
                "--device", paddleDevice,
                "--warmup-count", "1",
            ]),
        counters,
        workerDebug ? line => Console.Error.WriteLine($"[paddle] {line}") : null);
    var runtime = await worker.InitializeAsync(CancellationToken.None);
    var engine = new UnifiedPaddleOcrEngine(
        new PaddleRecognitionOcrEngine(worker),
        adaptive,
        counters);
    var rows = await new ProductionOcrCalibrationEvaluator().EvaluateAsync(
        engine,
        fixtures,
        CancellationToken.None);

    var rawExact = rows.Count(row => row.RawExactMatch);
    var normalizedExact = rows.Count(row => row.NormalizedMatch);
    var trusted = rows.Count(row => row.IsTrustedForSemantics);
    var trustedWrong = rows.Count(row => row.IsTrustedForSemantics && !row.NormalizedMatch);
    var polarityTerms = new HashSet<string>(
        ["好", "不好", "行", "不行", "可以", "不可以", "要", "不要", "是", "不是", "有", "没有"],
        StringComparer.Ordinal);
    var polarityRows = rows.Where(row => polarityTerms.Contains(row.Expected)).ToArray();
    var polarityErrors = polarityRows.Count(row => !row.NormalizedMatch);
    var missingPolarity = polarityTerms
        .Except(polarityRows.Select(row => row.Expected), StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
    var acceptanceCorpusReady = rows.Count >= 50 && missingPolarity.Length == 0;
    var routeSummaries = rows
        .GroupBy(row => row.Route)
        .Select(group => new
        {
            route = group.Key,
            count = group.Count(),
            raw_exact = group.Count(row => row.RawExactMatch),
            normalized_exact = group.Count(row => row.NormalizedMatch),
            semantic_ready = group.Count(row => row.IsTrustedForSemantics),
            trusted_wrong = group.Count(row => row.IsTrustedForSemantics && !row.NormalizedMatch),
        })
        .ToArray();
    var singleLineRows = rows.Where(row => row.Diagnostics?.Extraction?.DetectedLineCount <= 1).ToArray();
    var latencies = rows.Select(row => row.TotalOcr.TotalMilliseconds).Order().ToArray();
    var inference = rows.Where(row => row.PaddleInference is not null)
        .Select(row => row.PaddleInference!.Value.TotalMilliseconds).Order().ToArray();
    var markdown = new StringBuilder()
        .AppendLine("# Phase 4.5 production OCR calibration")
        .AppendLine()
        .AppendLine("> Paddle `rec_score` is uncalibrated diagnostic metadata. It is not `OcrConfidence` and never establishes trust by itself.")
        .AppendLine()
        .AppendLine($"- Runtime: Windows native Python; models `{runtime.DetectorModel}` + `{runtime.RecognizerModel}`; PaddleOCR `{runtime.PaddleOcrVersion}`; PaddlePaddle `{runtime.PaddlePaddleVersion}`; device `{runtime.ActiveDevice}`.")
        .AppendLine($"- Worker startup: {runtime.StartupElapsed.TotalMilliseconds:F1} ms; warmup: {runtime.WarmupElapsed.TotalMilliseconds:F1} ms.")
        .AppendLine($"- Overall raw exact: {rawExact}/{rows.Count}; normalized exact: {normalizedExact}/{rows.Count}.")
        .AppendLine($"- Semantic-ready coverage: {trusted}/{rows.Count}; trusted-wrong: {trustedWrong}.")
        .AppendLine(
            $"- Whole-bubble (0/1 detected lines) semantic-ready: {singleLineRows.Count(row => row.IsTrustedForSemantics)}/{singleLineRows.Length}. No normal secondary engine; trust calibration is deferred.")
        .AppendLine($"- Polarity/negation: {polarityRows.Length - polarityErrors}/{polarityRows.Length} normalized exact; errors: {polarityErrors}.")
        .AppendLine(
            $"- Acceptance-corpus gate: {(acceptanceCorpusReady ? "ready" : "incomplete")}; " +
            $"samples={rows.Count}/50 minimum; missing polarity labels=" +
            (missingPolarity.Length == 0 ? "none" : string.Join(", ", missingPolarity.Select(TableCell))) + ".")
        .AppendLine($"- Fallbacks: {counters.Snapshot.PaddleFallbacks}.")
        .AppendLine($"- Total OCR latency p50/p95: {Percentile(latencies, 0.50):F1}/{Percentile(latencies, 0.95):F1} ms.")
        .AppendLine($"- Paddle inference latency p50/p95: {Percentile(inference, 0.50):F1}/{Percentile(inference, 0.95):F1} ms.")
        .AppendLine();
    foreach (var route in routeSummaries)
    {
        markdown.AppendLine(
            $"- Route `{route.route}`: raw exact {route.raw_exact}/{route.count}; normalized exact " +
            $"{route.normalized_exact}/{route.count}; semantic-ready {route.semantic_ready}/{route.count}; " +
            $"trusted-wrong {route.trusted_wrong}.");
    }

    markdown
        .AppendLine()
        .AppendLine("| fixture | expected | route | paddle_raw | paddle_rec_score | secondary_raw | final_text | final_status/trust | raw_exact_match | normalized_match | raw_CER | normalized_CER | paddle_inference_ms | total_ocr_ms |")
        .AppendLine("| --- | --- | --- | --- | ---: | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: |");
    foreach (var row in rows)
    {
        markdown.AppendLine(
            $"| {TableCell(row.Fixture)} | {TableCell(row.Expected)} | {row.Route} | " +
            $"{TableCell(row.PaddleRaw ?? string.Empty)} | {(row.PaddleRecScore?.ToString("F4") ?? "n/a")} | " +
            $"{TableCell(row.SecondaryRaw ?? string.Empty)} | {TableCell(row.FinalText)} | " +
            $"{row.FinalStatus}/{row.IsTrustedForSemantics.ToString().ToLowerInvariant()} ({row.TrustBasis}) | " +
            $"{row.RawExactMatch.ToString().ToLowerInvariant()} | {row.NormalizedMatch.ToString().ToLowerInvariant()} | " +
            $"{row.RawCharacterErrorRate:F3} | {row.NormalizedCharacterErrorRate:F3} | " +
            $"{(row.PaddleInference?.TotalMilliseconds.ToString("F1") ?? "n/a")} | {row.TotalOcr.TotalMilliseconds:F1} |");
    }

    var fullOutputPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
    await File.WriteAllTextAsync(fullOutputPath, markdown.ToString());
    var jsonPath = Path.ChangeExtension(fullOutputPath, ".json");
    await File.WriteAllTextAsync(
        jsonPath,
        JsonSerializer.Serialize(
            new
            {
                runtime,
                counters = counters.Snapshot,
                summary = new
                {
                    count = rows.Count,
                    raw_exact = rawExact,
                    normalized_exact = normalizedExact,
                    semantic_ready = trusted,
                    trusted_wrong = trustedWrong,
                    polarity_count = polarityRows.Length,
                    polarity_errors = polarityErrors,
                    missing_polarity_labels = missingPolarity,
                    acceptance_corpus_ready = acceptanceCorpusReady,
                    total_ocr_p50_ms = Percentile(latencies, 0.50),
                    total_ocr_p95_ms = Percentile(latencies, 0.95),
                    paddle_inference_p50_ms = Percentile(inference, 0.50),
                    paddle_inference_p95_ms = Percentile(inference, 0.95),
                    single_line_semantic_ready = singleLineRows.Count(row => row.IsTrustedForSemantics),
                    by_route = routeSummaries,
                },
                rows,
            },
            new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(markdown);
    Console.WriteLine($"Production OCR report: {fullOutputPath}");
    Console.WriteLine($"Production OCR JSON: {jsonPath}");
    return trustedWrong == 0 && acceptanceCorpusReady ? 0 : 8;
}

static double Percentile(IReadOnlyList<double> values, double probability)
{
    if (values.Count == 0)
    {
        return 0;
    }

    var position = (values.Count - 1) * probability;
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    return lower == upper
        ? values[lower]
        : values[lower] + ((values[upper] - values[lower]) * (position - lower));
}

static async Task<int> CollectOcrCalibrationAsync(
    string expectedTextPath,
    string calibrationDirectory,
    string? sideOption,
    int skippedBubbles)
{
    var expected = (await File.ReadAllLinesAsync(Path.GetFullPath(expectedTextPath)))
        .Select(line => line.TrimEnd('\r', '\n'))
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToArray();
    if (expected.Length == 0)
    {
        throw new InvalidOperationException("Expected-text file contains no non-empty lines.");
    }

    MessageSide? side = sideOption?.ToLowerInvariant() switch
    {
        null => null,
        "self" => MessageSide.Self,
        "remote" => MessageSide.Remote,
        _ => throw new ArgumentException("--calibration-side must be self or remote."),
    };
    var window = new Win32WeChatWindowTracker().Locate()
        ?? throw new InvalidOperationException("WeChat window was not found.");
    if (window.IsMinimized || !window.IsVisible)
    {
        throw new InvalidOperationException("Restore the visible WeChat window before collecting calibration crops.");
    }

    var frame = new Win32ScreenRegionCapture().Capture(window);
    var detection = new BubbleDetectionPipeline(
        new DarkThemeChatRegionLocator(),
        new DarkThemeBubbleDetector()).Analyze(frame);
    var matchingBubbles = detection.Bubbles
        .Where(bubble => side is null || bubble.Side == side.Value)
        .OrderBy(bubble => bubble.Bounds.Y)
        .ToArray();
    var requiredVisibleBubbles = expected.Length + skippedBubbles;
    if (matchingBubbles.Length != requiredVisibleBubbles)
    {
        throw new InvalidOperationException(
            $"Detected {matchingBubbles.Length} matching text bubbles but expected " +
            $"{skippedBubbles} skipped + {expected.Length} labeled bubbles ({requiredVisibleBubbles} total). " +
            "Adjust the viewport or side filter; no crops were saved.");
    }

    var bubbles = matchingBubbles.Skip(skippedBubbles).ToArray();

    var fullDirectory = Path.GetFullPath(calibrationDirectory);
    var cropDirectory = Path.Combine(fullDirectory, "crops");
    Directory.CreateDirectory(cropDirectory);
    var manifestPath = Path.Combine(fullDirectory, "manifest.json");
    var manifest = File.Exists(manifestPath)
        ? JsonSerializer.Deserialize<OcrEvaluationManifest>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new OcrEvaluationManifest([])
        : new OcrEvaluationManifest([]);
    var batchId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    for (var index = 0; index < bubbles.Length; index++)
    {
        var name = $"real-{batchId}-{index + 1:D2}";
        var cropPath = Path.Combine(cropDirectory, $"{name}.png");
        var crop = new ImageCrop(frame, bubbles[index].Bounds);
        PngFrameWriter.Save(ImageCropExtractor.Extract(crop), cropPath);
        manifest.Fixtures.Add(new OcrEvaluationManifestItem(
            name,
            Image: null,
            Crop: Path.GetRelativePath(fullDirectory, cropPath),
            expected[index],
            0, 0, 0, 0,
            Group: "phase4.5-real",
            CaptureDpi: window.Dpi.X,
            DpiScale: window.Dpi.ScaleX,
            OcrRoute: OcrRoute.PaddleUnified.ToString(),
            Monitor: window.Monitor.DeviceName));
    }

    await File.WriteAllTextAsync(
        manifestPath,
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(
        $"Skipped {skippedBubbles} leading matching bubble(s); " +
        $"saved {bubbles.Length} private crop(s) under {cropDirectory}.");
    Console.WriteLine($"Calibration manifest now contains {manifest.Fixtures.Count} sample(s): {manifestPath}");
    Console.WriteLine("Review every crop-to-expected pairing before using it as acceptance evidence.");
    return 0;
}

static async Task<int> ObserveWeChatAsync(string[] arguments)
{
    var intervalMilliseconds = PositiveIntOption(arguments, "--interval-ms", 200)!.Value;
    var durationSeconds = PositiveIntOption(arguments, "--observe-seconds", null);
    var debugText = FindOption(arguments, "--debug-text") >= 0;
    var paddleWorkerDebug = FindOption(arguments, "--paddle-worker-debug") >= 0;
    var paddleDevice = OptionValue(arguments, "--paddle-device") ?? "gpu:0";
    var paddlePython = OptionValue(arguments, "--paddle-python")
        ?? Environment.GetEnvironmentVariable("WECHAT_JEV_PADDLE_PYTHON")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatJevHud",
            "paddle-ocr",
            ".venv",
            "Scripts",
            "python.exe");
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
    var productionOcrCounters = new ProductionOcrCounters();
    var paddleWorker = new PaddleWorkerClient(
        PaddleWorkerOptions.Create(
            paddlePython,
            [
                Path.Combine(Environment.CurrentDirectory, "scripts", "paddle_ocr_worker.py"),
                "--model",
                "PP-OCRv6_small_rec",
                "--device",
                paddleDevice,
                "--warmup-count",
                "1",
            ]),
        productionOcrCounters,
        paddleWorkerDebug ? line => Console.Error.WriteLine($"[paddle] {line}") : null);
    await using var paddleWorkerLifetime = paddleWorker;
    try
    {
        var runtime = await paddleWorker.InitializeAsync(CancellationToken.None);
        Console.WriteLine(
            $"Paddle READY detector_model={runtime.DetectorModel} recognizer_model={runtime.RecognizerModel} paddleocr={runtime.PaddleOcrVersion} " +
            $"paddle={runtime.PaddlePaddleVersion} requested_device={runtime.RequestedDevice} " +
            $"active_device={runtime.ActiveDevice} worker_startup_ms={runtime.StartupElapsed.TotalMilliseconds:F1} " +
            $"worker_warmup_ms={runtime.WarmupElapsed.TotalMilliseconds:F1}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"Paddle worker unavailable ({exception.Message}); observer will use Adaptive OCR fallback.");
    }

    var unifiedOcr = new UnifiedPaddleOcrEngine(
        new PaddleRecognitionOcrEngine(paddleWorker),
        adaptive,
        productionOcrCounters);
    IMessageObserver observer = new MessageObserver(
        new DarkThemeChatRegionLocator(),
        new DarkThemeBubbleDetector(),
        unifiedOcr,
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
                $"status={eventArgs.Message.OcrStatus} semantic_ready={eventArgs.Message.IsTrustedForSemantics.ToString().ToLowerInvariant()} {ExtractionDiagnostic(eventArgs.Message)}");
        }
    };
    observer.NewMessageObserved += (_, eventArgs) =>
    {
        Console.WriteLine(
            $"[epoch {eventArgs.Message.ConversationEpochId}] NEW {eventArgs.Message.Side} " +
            $"{DiagnosticText(eventArgs.Message, debugText)} id={eventArgs.Message.Id} " +
            $"status={eventArgs.Message.OcrStatus} semantic_ready={eventArgs.Message.IsTrustedForSemantics.ToString().ToLowerInvariant()} {ExtractionDiagnostic(eventArgs.Message)}");
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
                    PrintBaselineObservation(result);
                    foreach (var match in result.OccurrenceMatches ?? [])
                    {
                        if (match.AmbiguousOccurrenceCount > 1)
                            Console.WriteLine($"occurrence previous_id={match.PreviousId} previous_y={match.PreviousY} " +
                                $"candidate_y={match.CandidateY} estimated_delta_y={match.EstimatedDeltaY:F2} " +
                                $"match_cost={match.MatchCost:F3} ambiguous_occurrence_count={match.AmbiguousOccurrenceCount}");
                    }
                    foreach (var visibility in result.BubbleVisibility ?? [])
                        Console.WriteLine($"bubble_visibility bounds={visibility.BubbleBounds} chat_roi={visibility.ChatRoi} " +
                            $"complete={visibility.IsFullyVisible.ToString().ToLowerInvariant()}");
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
    PrintProductionOcrCounters(productionOcrCounters.Snapshot, paddleWorker.RuntimeInfo);
    Console.WriteLine($"idle_window_frames={stableWindowUnchangedFrames}");
    Console.WriteLine($"idle_window_seconds={stableWindow.Elapsed.TotalSeconds:F2}");
    Console.WriteLine($"idle_process_cpu_percent={stableCpuPercent:F2}");
    return 0;
}

static void PrintProductionOcrCounters(
    ProductionOcrCounterSnapshot counters,
    PaddleWorkerRuntimeInfo? runtime)
{
    Console.WriteLine("production OCR counters:");
    Console.WriteLine($"paddle_worker_starts={counters.PaddleWorkerStarts}");
    Console.WriteLine($"paddle_worker_restarts={counters.PaddleWorkerRestarts}");
    Console.WriteLine($"paddle_requests={counters.PaddleRequests}");
    Console.WriteLine($"paddle_failures={counters.PaddleFailures}");
    Console.WriteLine($"paddle_timeouts={counters.PaddleTimeouts}");
    Console.WriteLine($"paddle_fallbacks={counters.PaddleFallbacks}");
    Console.WriteLine($"paddle_inference_ms={counters.PaddleInference.TotalMilliseconds:F1}");
    Console.WriteLine($"paddle_roundtrip_ms={counters.PaddleRoundtrip.TotalMilliseconds:F1}");
    Console.WriteLine(
        $"paddle_transport_ms={Math.Max(0, (counters.PaddleRoundtrip - counters.PaddleWorkerTotal).TotalMilliseconds):F1}");
    if (runtime is not null)
    {
        Console.WriteLine($"worker_startup_ms={runtime.StartupElapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"worker_warmup_ms={runtime.WarmupElapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"paddle_active_device={runtime.ActiveDevice}");
    }
}

static string ExtractionDiagnostic(ObservedMessage message)
{
    var diagnostics = message.OcrDiagnostics;
    var extraction = diagnostics?.Extraction;
    return $"fully_visible={message.IsFullyVisible.ToString().ToLowerInvariant()} complete_text={message.HasCompleteText.ToString().ToLowerInvariant()} " +
        $"detected_lines={extraction?.DetectedLineCount.ToString() ?? "n/a"} " +
        $"ocr_ms={diagnostics?.TotalElapsed.TotalMilliseconds:F1} " +
        $"detection_ms={extraction?.DetectionElapsed.TotalMilliseconds:F1} " +
        $"recognition_ms={extraction?.RecognitionElapsed.TotalMilliseconds:F1} " +
        $"adaptive_fallback={diagnostics?.RuntimeFallback.ToString().ToLowerInvariant()} " +
        $"quote_separation_unverified={diagnostics?.QuoteSeparationUnverified.ToString().ToLowerInvariant()}";
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

    var evidence =
        $"identity_evidence=\"{identity.ProviderDiagnostics}\" " +
        $"title_visual_distance={identity.TitleVisualDistance?.ToString("F4") ?? "n/a"} " +
        $"title_aspect_distance={identity.TitleAspectDistance?.ToString("F4") ?? "n/a"} " +
        $"previous_visible_strong_overlap={identity.PreviousVisibleStrongOverlap}/{identity.VisibleCandidates} " +
        $"previous_visible_weak_overlap={identity.PreviousVisibleWeakOverlap}/{identity.VisibleCandidates} " +
        $"trusted_text_overlap={identity.TrustedTextOverlap} " +
        $"live_tail_strong_match={identity.LiveTailStrongMatch.ToString().ToLowerInvariant()} " +
        $"live_tail_weak_match={identity.LiveTailWeakMatch.ToString().ToLowerInvariant()} " +
        $"history_only_matches={identity.HistoryOnlyMatches}";
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

static void PrintBaselineObservation(ObservationResult result)
{
    if (result.Baseline.State == ConversationBaselineState.AwaitingInitialSnapshot)
    {
        Console.WriteLine(
            $"[epoch {result.Epoch.Id}] awaiting initial snapshot " +
            $"non_empty_observations={result.Baseline.InitialSnapshotObservations}/" +
            $"{result.Baseline.RequiredInitialSnapshotObservations} " +
            $"empty_observations={result.Baseline.EmptyObservations}/" +
            $"{result.Baseline.RequiredEmptyObservations}");
        return;
    }

    if (!result.Baseline.EstablishedThisFrame)
    {
        return;
    }

    var kind = result.Baseline.EmptyObservations >= result.Baseline.RequiredEmptyObservations
        ? "empty baseline established"
        : "initial snapshot established";
    Console.WriteLine($"[epoch {result.Epoch.Id}] {kind}");
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

static int NonNegativeIntOption(string[] arguments, string option, int defaultValue)
{
    var raw = OptionValue(arguments, option);
    if (raw is null)
    {
        return defaultValue;
    }

    if (!int.TryParse(raw, out var parsed) || parsed < 0)
    {
        throw new ArgumentException($"{option} must be a non-negative integer.");
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

static OcrCropRole ParseCropRole(string? role) => role?.ToLowerInvariant() switch
{
    null or "main_message" => OcrCropRole.MainMessage,
    "quoted_text" => OcrCropRole.QuotedText,
    _ => throw new ArgumentException($"Unsupported OCR crop role '{role}'."),
};

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
    string? Image,
    string? Crop,
    string Expected,
    int X,
    int Y,
    int Width,
    int Height,
    string? Group = null,
    string? Role = null,
    uint? CaptureDpi = null,
    double? DpiScale = null,
    string? OcrRoute = null,
    string? Monitor = null);
