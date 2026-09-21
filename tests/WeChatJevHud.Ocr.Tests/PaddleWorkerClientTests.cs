using System.IO;
using Xunit;

namespace WeChatJevHud.Ocr.Tests;

public sealed class PaddleWorkerClientTests
{
    [Fact]
    public async Task PaddleScoreIsPreservedWithoutBecomingOcrConfidenceOrTrust()
    {
        await using var client = new StubRecognitionClient(
            new PaddleRecognition(
                "request-1",
                "好",
                0.999,
                TimeSpan.FromMilliseconds(4),
                TimeSpan.FromMilliseconds(7)));
        var crop = TestCrop();

        var result = await new PaddleRecognitionOcrEngine(client)
            .RecognizeAsync(crop, CancellationToken.None);

        Assert.Null(result.OcrConfidence);
        Assert.Equal(OcrTextStatus.LowConfidence, result.Status);
        Assert.False(result.IsTrustedForSemantics);
        var evidence = Assert.Single(result.Diagnostics!.Evidence);
        Assert.Equal("paddle_rec_score", evidence.EngineScoreKind);
        Assert.Equal(0.999, evidence.EngineScore);
        Assert.Null(evidence.OcrConfidence);
    }

    [Fact]
    public async Task HandshakeAndSequentialRequestsUseOneWorkerAndCorrelateIds()
    {
        await using var client = Client("normal");

        var ready = await client.InitializeAsync(CancellationToken.None);
        var first = await client.RecognizeAsync([1, 2, 3], CancellationToken.None);
        var second = await client.RecognizeAsync([4, 5, 6], CancellationToken.None);

        Assert.Equal("PP-OCRv6_small_rec", ready.ModelName);
        Assert.Equal("cpu", ready.ActiveDevice);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal("好", first.RawText);
        Assert.Equal(1, client.Counters.Snapshot.PaddleWorkerStarts);
        Assert.Equal(2, client.Counters.Snapshot.PaddleRequests);
    }

    [Fact]
    public async Task MalformedResponseFailsWithoutReturningText()
    {
        await using var client = Client("malformed");

        await Assert.ThrowsAsync<PaddleWorkerException>(
            () => client.RecognizeAsync([1], CancellationToken.None));

        Assert.Equal(1, client.Counters.Snapshot.PaddleFailures);
    }

    [Fact]
    public async Task RequestTimeoutStopsWorkerAndIsCounted()
    {
        await using var client = Client("timeout", TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<PaddleWorkerException>(
            () => client.RecognizeAsync([1], CancellationToken.None));

        Assert.Equal(1, client.Counters.Snapshot.PaddleTimeouts);
    }

    [Fact]
    public async Task CrashFailsAndNextRequestRestartsWorker()
    {
        await using var client = Client("crash");

        await Assert.ThrowsAsync<PaddleWorkerException>(
            () => client.RecognizeAsync([1], CancellationToken.None));
        await Assert.ThrowsAsync<PaddleWorkerException>(
            () => client.RecognizeAsync([2], CancellationToken.None));

        Assert.Equal(2, client.Counters.Snapshot.PaddleWorkerStarts);
        Assert.Equal(1, client.Counters.Snapshot.PaddleWorkerRestarts);
    }

    [Fact]
    public async Task RequestedGpuThatActivatesCpuFailsTheHandshake()
    {
        await using var client = Client("gpu-fallback");

        var exception = await Assert.ThrowsAsync<PaddleWorkerException>(
            () => client.InitializeAsync(CancellationToken.None));

        Assert.Contains("requested gpu:0", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.Counters.Snapshot.PaddleFailures);
    }

    [Fact]
    public async Task WorkerDiagnosticsNeverForwardRawStderrContent()
    {
        var diagnostics = new List<string>();
        await using var client = Client("stderr", log: diagnostics.Add);

        await client.InitializeAsync(CancellationToken.None);
        await client.RecognizeAsync([1], CancellationToken.None);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("category=info", diagnostic, StringComparison.Ordinal);
        Assert.Contains("chars=35", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-message", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private bubble text", diagnostic, StringComparison.Ordinal);
    }

    private static PaddleWorkerClient Client(
        string mode,
        TimeSpan? requestTimeout = null,
        Action<string>? log = null)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_paddle_worker.ps1");
        return new PaddleWorkerClient(
            PaddleWorkerOptions.Create(
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", mode],
                startupTimeout: TimeSpan.FromSeconds(5),
                requestTimeout: requestTimeout ?? TimeSpan.FromSeconds(2),
                restartCooldown: TimeSpan.Zero),
            log: log);
    }

    private static ImageCrop TestCrop()
    {
        var pixels = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray();
        var frame = new WeChatJevHud.Capture.CapturedFrame(
            16,
            16,
            64,
            pixels,
            new WeChatJevHud.Core.Geometry.DesktopPixelRect(0, 0, 16, 16),
            WeChatJevHud.Capture.CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
        return new ImageCrop(frame, new WeChatJevHud.Core.Geometry.CapturePixelRect(0, 0, 16, 16));
    }

    private sealed class StubRecognitionClient(PaddleRecognition recognition) : IPaddleRecognitionClient
    {
        public PaddleWorkerRuntimeInfo? RuntimeInfo { get; } = new(
            "PP-OCRv6_small_rec", "test", "test", "cpu", "cpu", TimeSpan.Zero, TimeSpan.Zero);

        public ProductionOcrCounters Counters { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<PaddleWorkerRuntimeInfo> InitializeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(RuntimeInfo!);

        public Task<PaddleRecognition> RecognizeAsync(byte[] pngBytes, CancellationToken cancellationToken) =>
            Task.FromResult(recognition);
    }
}
