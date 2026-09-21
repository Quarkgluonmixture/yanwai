using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Ocr.Tests;

public sealed class AdaptiveOcrEngineTests
{
    [Fact]
    public async Task RecognizeAsync_accepts_the_highest_confidence_candidate_at_threshold()
    {
        var lower = new StubOcrEngine("lower", new OcrResult("候选一", 0.78, OcrTextStatus.Recognized, "候选一"));
        var higher = new StubOcrEngine("higher", new OcrResult("候选二", 0.90, OcrTextStatus.Recognized, "候选二"));
        var fallback = new StubOcrEngine("fallback", new OcrResult("回退", null, OcrTextStatus.Recognized, "回退"));
        var engine = new AdaptiveOcrEngine([lower, higher], fallback, confidenceThreshold: 0.90);

        var result = await engine.RecognizeAsync(Crop(), CancellationToken.None);

        Assert.Equal("候选二", result.Text);
        Assert.Equal(0.90, result.OcrConfidence);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task RecognizeAsync_uses_uncertain_fallback_for_a_candidate_below_the_safe_threshold()
    {
        var first = new StubOcrEngine("first", new OcrResult("错一", 0.20, OcrTextStatus.LowConfidence, "错一"));
        var second = new StubOcrEngine("second", new OcrResult("错二", 0.87, OcrTextStatus.Recognized, "错二"));
        var fallback = new StubOcrEngine("fallback", new OcrResult("短文本", null, OcrTextStatus.Recognized, "短文本"));
        var engine = new AdaptiveOcrEngine([first, second], fallback, confidenceThreshold: 0.90);

        var result = await engine.RecognizeAsync(Crop(), CancellationToken.None);

        Assert.Equal("短文本", result.Text);
        Assert.Null(result.OcrConfidence);
        Assert.Equal(OcrTextStatus.LowConfidence, result.Status);
        Assert.Equal(1, fallback.CallCount);
    }

    private static ImageCrop Crop()
    {
        var frame = new CapturedFrame(
            20,
            10,
            80,
            new byte[800],
            new DesktopPixelRect(0, 0, 20, 10),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
        return new ImageCrop(frame, new CapturePixelRect(0, 0, 20, 10));
    }

    private sealed class StubOcrEngine(string name, OcrResult result) : IOcrEngine
    {
        public string Name => name;

        public int CallCount { get; private set; }

        public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
