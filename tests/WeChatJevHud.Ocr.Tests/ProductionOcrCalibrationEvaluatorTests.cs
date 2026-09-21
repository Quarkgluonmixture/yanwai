using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using Xunit;

namespace WeChatJevHud.Ocr.Tests;

public sealed class ProductionOcrCalibrationEvaluatorTests
{
    [Fact]
    public async Task ReportsEngineSpecificEvidenceAndLiteralAccuracySeparately()
    {
        var diagnostics = new OcrDiagnostics(
            OcrRoute.PaddleSingleLine,
            OcrTrustBasis.None,
            [
                new OcrEngineEvidence(
                    "PP-OCRv6_small_rec", "Hello,world", OcrTextStatus.LowConfidence,
                    null, 0.99, "paddle_rec_score", TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(7)),
                new OcrEngineEvidence(
                    "adaptive-ocr", "Hello, world", OcrTextStatus.LowConfidence,
                    null, null, null, null, null),
            ],
            TimeSpan.FromMilliseconds(10));
        var engine = new StubEngine(
            new OcrResult("Hello,world", null, OcrTextStatus.LowConfidence, "Hello,world", diagnostics));

        var rows = await new ProductionOcrCalibrationEvaluator().EvaluateAsync(
            engine,
            [new OcrEvaluationFixture("latin", "Hello, world", Crop())],
            CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.False(row.RawExactMatch);
        Assert.False(row.NormalizedMatch);
        Assert.Equal(0.99, row.PaddleRecScore);
        Assert.Null(diagnostics.Evidence[0].OcrConfidence);
        Assert.False(row.IsTrustedForSemantics);
    }

    private static ImageCrop Crop()
    {
        var pixels = Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray();
        var frame = new CapturedFrame(
            8, 8, 32, pixels,
            new DesktopPixelRect(0, 0, 8, 8),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
        return new ImageCrop(frame, new CapturePixelRect(0, 0, 8, 8));
    }

    private sealed class StubEngine(OcrResult result) : IOcrEngine
    {
        public string Name => "stub";

        public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
