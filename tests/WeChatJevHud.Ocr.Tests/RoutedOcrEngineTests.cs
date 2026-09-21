using System.IO;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using Xunit;

namespace WeChatJevHud.Ocr.Tests;

public sealed class RoutedOcrEngineTests
{
    [Fact]
    public async Task AgreementPromotesTextWithoutTreatingPaddleScoreAsConfidence()
    {
        var paddle = new StubEngine(
            "PP-OCRv6_small_rec",
            new OcrResult(
                "好",
                null,
                OcrTextStatus.LowConfidence,
                "好",
                PaddleDiagnostics("好", 0.999)));
        var adaptive = new StubEngine(
            "adaptive-ocr",
            new OcrResult(
                "好",
                null,
                OcrTextStatus.LowConfidence,
                "好",
                AdaptiveDiagnostics("好", 0.72)));
        var engine = new RoutedOcrEngine(new FixedRoute(OcrRoute.PaddleSingleLine), paddle, adaptive);

        var result = await engine.RecognizeAsync(Crop(), CancellationToken.None);

        Assert.Equal(OcrTextStatus.Recognized, result.Status);
        Assert.True(result.IsTrustedForSemantics);
        Assert.Null(result.OcrConfidence);
        Assert.Equal(OcrTrustBasis.IndependentEngineAgreement, result.Diagnostics!.TrustBasis);
        var paddleEvidence = Assert.Single(result.Diagnostics.Evidence, value => value.EngineScoreKind == "paddle_rec_score");
        Assert.Equal(0.999, paddleEvidence.EngineScore);
        Assert.Null(paddleEvidence.OcrConfidence);
    }

    [Fact]
    public async Task DisagreementKeepsPaddleOnlyOutputUntrustedEvenWithHighScore()
    {
        var paddle = new StubEngine(
            "PP-OCRv6_small_rec",
            new OcrResult("不要", null, OcrTextStatus.LowConfidence, "不要", PaddleDiagnostics("不要", 0.9999)));
        var adaptive = new StubEngine(
            "adaptive-ocr",
            new OcrResult("要", null, OcrTextStatus.LowConfidence, "要"));
        var engine = new RoutedOcrEngine(new FixedRoute(OcrRoute.PaddleSingleLine), paddle, adaptive);

        var result = await engine.RecognizeAsync(Crop(), CancellationToken.None);

        Assert.Equal("不要", result.Text);
        Assert.Equal(OcrTextStatus.LowConfidence, result.Status);
        Assert.False(result.IsTrustedForSemantics);
        Assert.Null(result.OcrConfidence);
        Assert.Equal(OcrTrustBasis.None, result.Diagnostics!.TrustBasis);
    }

    [Fact]
    public async Task TwoUncalibratedEnginesAgreeingDoesNotEstablishTrust()
    {
        var paddle = new StubEngine(
            "PP-OCRv6_small_rec",
            new OcrResult("没事哒", null, OcrTextStatus.LowConfidence, "没事哒", PaddleDiagnostics("没事哒", 0.999)));
        var adaptive = new StubEngine(
            "adaptive-ocr",
            new OcrResult("没事哒", null, OcrTextStatus.LowConfidence, "没 事 哒"));
        var engine = new RoutedOcrEngine(new FixedRoute(OcrRoute.PaddleSingleLine), paddle, adaptive);

        var result = await engine.RecognizeAsync(Crop(), CancellationToken.None);

        Assert.Equal(OcrTextStatus.LowConfidence, result.Status);
        Assert.False(result.IsTrustedForSemantics);
        Assert.Equal(OcrTrustBasis.None, result.Diagnostics!.TrustBasis);
    }

    [Fact]
    public async Task PaddleFailureFallsBackToAdaptive()
    {
        var paddle = new ThrowingEngine();
        var adaptive = new StubEngine(
            "adaptive-ocr",
            new OcrResult("fallback", 0.96, OcrTextStatus.Recognized, "fallback"));
        var counters = new ProductionOcrCounters();
        var engine = new RoutedOcrEngine(
            new FixedRoute(OcrRoute.PaddleSingleLine), paddle, adaptive, counters);

        var result = await engine.RecognizeAsync(Crop(), CancellationToken.None);

        Assert.Equal("fallback", result.Text);
        Assert.True(result.IsTrustedForSemantics);
        Assert.Equal(OcrTrustBasis.AdaptiveTrusted, result.Diagnostics!.TrustBasis);
        Assert.Equal(1, counters.Snapshot.PaddleFallbacks);
    }

    [Fact]
    public async Task MultilineRouteDoesNotCallPaddle()
    {
        var paddle = new StubEngine("paddle", new OcrResult("unused", null, OcrTextStatus.LowConfidence, "unused"));
        var adaptive = new StubEngine("adaptive", new OcrResult("line one\nline two", 0.95, OcrTextStatus.Recognized, "line one\nline two"));
        var engine = new RoutedOcrEngine(new FixedRoute(OcrRoute.Adaptive), paddle, adaptive);

        var result = await engine.RecognizeAsync(Crop(), CancellationToken.None);

        Assert.Equal(0, paddle.Calls);
        Assert.Equal(1, adaptive.Calls);
        Assert.Equal(OcrRoute.Adaptive, result.Diagnostics!.Route);
    }

    private static OcrDiagnostics PaddleDiagnostics(string text, double score) =>
        new(
            OcrRoute.PaddleSingleLine,
            OcrTrustBasis.None,
            [new OcrEngineEvidence(
                "PP-OCRv6_small_rec",
                text,
                OcrTextStatus.LowConfidence,
                null,
                score,
                "paddle_rec_score",
                TimeSpan.FromMilliseconds(4),
                TimeSpan.FromMilliseconds(7))],
            TimeSpan.FromMilliseconds(7));

    private static OcrDiagnostics AdaptiveDiagnostics(string text, double confidence) =>
        new(
            OcrRoute.Adaptive,
            OcrTrustBasis.None,
            [new OcrEngineEvidence(
                "tesseract-test",
                text,
                OcrTextStatus.LowConfidence,
                confidence,
                null,
                null,
                null,
                null)],
            TimeSpan.Zero);

    private static ImageCrop Crop()
    {
        var pixels = Enumerable.Repeat((byte)255, 80 * 40 * 4).ToArray();
        return new ImageCrop(
            new CapturedFrame(
                80,
                40,
                320,
                pixels,
                new DesktopPixelRect(0, 0, 80, 40),
                CaptureMethod.RenderWindow,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero),
            new CapturePixelRect(0, 0, 80, 40));
    }

    private sealed class FixedRoute(OcrRoute route) : IOcrRoutingPolicy
    {
        public OcrRoute SelectRoute(ImageCrop crop) => route;
    }

    private sealed class StubEngine(string name, OcrResult result) : IOcrEngine
    {
        public int Calls { get; private set; }

        public string Name => name;

        public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingEngine : IOcrEngine
    {
        public string Name => "paddle";

        public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken) =>
            throw new IOException("worker unavailable");
    }
}
