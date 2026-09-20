using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Ocr.Tests;

public sealed class OcrEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_reports_engine_fixture_text_confidence_and_elapsed_time()
    {
        var frame = SolidFrame(80, 50);
        var crop = new ImageCrop(frame, new CapturePixelRect(10, 12, 30, 20));
        var engine = new RecordingOcrEngine(
            new OcrResult("中英 mixed", 0.82, OcrTextStatus.Recognized));
        var evaluator = new OcrEvaluator();

        var rows = await evaluator.EvaluateAsync(
            engine,
            [new OcrEvaluationFixture("mixed", "中英 mixed", crop)],
            CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("recording", row.Engine);
        Assert.Equal("mixed", row.Fixture);
        Assert.Equal("中英 mixed", row.Expected);
        Assert.Equal("中英 mixed", row.Recognized);
        Assert.Equal(0.82, row.OcrConfidence);
        Assert.True(row.Elapsed >= TimeSpan.Zero);
        Assert.Equal(crop, engine.LastCrop);
        Assert.True(row.ExactMatch);
        Assert.Equal(0, row.CharacterErrorRate);
    }

    [Fact]
    public async Task EvaluateAsync_reports_exact_match_and_normalized_character_error_rate()
    {
        var frame = SolidFrame(80, 50);
        var crop = new ImageCrop(frame, new CapturePixelRect(10, 12, 30, 20));
        var engine = new RecordingOcrEngine(
            new OcrResult("怎久说", null, OcrTextStatus.LowConfidence));

        var rows = await new OcrEvaluator().EvaluateAsync(
            engine,
            [new OcrEvaluationFixture("short", "怎么说", crop)],
            CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.False(row.ExactMatch);
        Assert.Equal(1d / 3d, row.CharacterErrorRate, precision: 10);
        Assert.Equal(OcrTextStatus.LowConfidence, row.Status);
    }

    private static CapturedFrame SolidFrame(int width, int height)
    {
        var stride = width * 4;
        return new CapturedFrame(
            width,
            height,
            stride,
            new byte[stride * height],
            new DesktopPixelRect(0, 0, width, height),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
    }

    private sealed class RecordingOcrEngine(OcrResult result) : IOcrEngine
    {
        public string Name => "recording";

        public ImageCrop? LastCrop { get; private set; }

        public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
        {
            LastCrop = crop;
            return Task.FromResult(result);
        }
    }
}
