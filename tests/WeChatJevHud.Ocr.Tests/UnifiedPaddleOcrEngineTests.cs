using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using Xunit;

namespace WeChatJevHud.Ocr.Tests;

public sealed class UnifiedPaddleOcrEngineTests
{
    [Theory]
    [InlineData("好", OcrTextStatus.LowConfidence)]
    [InlineData("", OcrTextStatus.NoText)]
    public async Task SuccessfulPaddleIncludingNoTextNeverCallsAdaptive(string text, OcrTextStatus status)
    {
        var paddle = new Stub(new OcrResult(text, null, status, text));
        var adaptive = new Stub(new OcrResult("wrong", 0.99, OcrTextStatus.Recognized, "wrong"));
        var counters = new ProductionOcrCounters();
        var result = await new UnifiedPaddleOcrEngine(paddle, adaptive, counters).RecognizeAsync(Crop(), default);
        Assert.Equal(text, result.RawText);
        Assert.False(result.IsTrustedForSemantics);
        Assert.Null(result.OcrConfidence);
        Assert.Equal(0, adaptive.Calls);
        Assert.Equal(0, counters.Snapshot.PaddleFallbacks);
    }

    [Fact]
    public async Task CallerCancellationDoesNotInvokeFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var paddle = new Stub(null);
        var adaptive = new Stub(new OcrResult("", null, OcrTextStatus.NoText, ""));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new UnifiedPaddleOcrEngine(paddle, adaptive).RecognizeAsync(Crop(), cancellation.Token));
        Assert.Equal(0, adaptive.Calls);
    }

    [Fact]
    public async Task ZeroDetectionRemainsPaddleAndQuoteIsIndependent()
    {
        await using var client = new Client();
        var adaptive = new Stub(new OcrResult("", null, OcrTextStatus.NoText, ""));
        var engine = new UnifiedPaddleOcrEngine(new PaddleRecognitionOcrEngine(client), adaptive);
        var main = await engine.RecognizeAsync(Crop(), default);
        var quote = await engine.RecognizeAsync(Crop() with { Role = OcrCropRole.QuotedText }, default);
        Assert.Equal(0, adaptive.Calls);
        Assert.Equal(2, client.Calls);
        Assert.Equal(0, main.Diagnostics!.Extraction!.DetectedLineCount);
        Assert.True(main.Diagnostics.QuoteSeparationUnverified);
        Assert.False(quote.Diagnostics!.QuoteSeparationUnverified);
        Assert.Equal(OcrCropRole.QuotedText, quote.Diagnostics.RegionRole);
        Assert.Null(main.OcrConfidence);
        Assert.False(main.IsTrustedForSemantics);
        Assert.Equal(0.9999, Assert.Single(main.Diagnostics.Evidence).EngineScore);
    }

    private static ImageCrop Crop() => new(new CapturedFrame(8, 8, 32, new byte[256],
        new DesktopPixelRect(0, 0, 8, 8), CaptureMethod.RenderWindow, DateTimeOffset.UtcNow, TimeSpan.Zero),
        new CapturePixelRect(0, 0, 8, 8));

    [Fact]
    public async Task BoxOutsideCropTriggersUntrustedFallback()
    {
        await using var client = new Client { InvalidBox = true };
        var adaptive = new Stub(new OcrResult("fallback", .95, OcrTextStatus.Recognized, "fallback"));
        var result = await new UnifiedPaddleOcrEngine(new PaddleRecognitionOcrEngine(client), adaptive)
            .RecognizeAsync(Crop(), default);
        Assert.Equal(1, adaptive.Calls);
        Assert.False(result.IsTrustedForSemantics);
        Assert.True(result.Diagnostics!.RuntimeFallback);
    }

    [Fact]
    public async Task TotalElapsedIncludesWrapperWorkAndPreservesWorkerTimings()
    {
        await using var client = new Client();
        var result = await new UnifiedPaddleOcrEngine(new PaddleRecognitionOcrEngine(client), new Stub(null))
            .RecognizeAsync(Crop(), default);
        Assert.True(result.Diagnostics!.TotalElapsed > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, Assert.Single(result.Diagnostics.Evidence).RoundtripElapsed);
    }

    private sealed class Stub(OcrResult? result) : IOcrEngine
    {
        public int Calls { get; private set; }
        public string Name => "stub";
        public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result!);
        }
    }

    private sealed class Client : IPaddleRecognitionClient
    {
        public bool InvalidBox { get; init; }
        public int Calls { get; private set; }
        public PaddleWorkerRuntimeInfo? RuntimeInfo => null;
        public ProductionOcrCounters Counters { get; } = new();
        public Task<PaddleWorkerRuntimeInfo> InitializeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaddleRecognition> RecognizeAsync(byte[] pngBytes, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PaddleRecognition(Calls.ToString(), "好", 0.9999, TimeSpan.Zero, TimeSpan.Zero,
                new UnifiedExtraction(InvalidBox ? 1 : 0, InvalidBox ? [new[] { 0, 0, 99, 99 }] : [],
                    [new PaddleLine("好", 0.9999)], TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
