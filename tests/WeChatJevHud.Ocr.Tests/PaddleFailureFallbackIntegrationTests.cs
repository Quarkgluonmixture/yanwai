using System.IO;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using Xunit;

namespace WeChatJevHud.Ocr.Tests;

public sealed class PaddleFailureFallbackIntegrationTests
{
    [Theory]
    [InlineData("malformed")]
    [InlineData("timeout")]
    [InlineData("crash")]
    [InlineData("gpu-fallback")]
    [InlineData("wrong-id")]
    [InlineData("detector-failure")]
    [InlineData("recognizer-failure")]
    public async Task ConcreteWorkerFailureFallsBackToAdaptive(string mode)
    {
        await using var worker = Worker(mode);
        var counters = worker.Counters;
        var engine = new UnifiedPaddleOcrEngine(
            new PaddleRecognitionOcrEngine(worker),
            new AdaptiveStub(),
            counters);

        var result = await engine.RecognizeAsync(Crop(), CancellationToken.None);

        Assert.Equal("安全回退", result.Text);
        Assert.Equal(OcrTextStatus.LowConfidence, result.Status);
        Assert.False(result.IsTrustedForSemantics);
        Assert.Equal(1, counters.Snapshot.PaddleFallbacks);
    }

    [Fact]
    public async Task UnavailableWorkerExecutableFallsBackToAdaptive()
    {
        await using var worker = new PaddleWorkerClient(
            PaddleWorkerOptions.Create(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"),
                [],
                restartCooldown: TimeSpan.Zero));
        var engine = new UnifiedPaddleOcrEngine(
            new PaddleRecognitionOcrEngine(worker),
            new AdaptiveStub(),
            worker.Counters);

        var result = await engine.RecognizeAsync(Crop(), CancellationToken.None);

        Assert.Equal("安全回退", result.Text);
        Assert.Equal(1, worker.Counters.Snapshot.PaddleFallbacks);
    }

    private static PaddleWorkerClient Worker(string mode)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_paddle_worker.ps1");
        return new PaddleWorkerClient(
            PaddleWorkerOptions.Create(
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", mode],
                startupTimeout: TimeSpan.FromSeconds(5),
                requestTimeout: TimeSpan.FromMilliseconds(100),
                restartCooldown: TimeSpan.Zero));
    }

    private static ImageCrop Crop()
    {
        var pixels = Enumerable.Repeat((byte)255, 32 * 24 * 4).ToArray();
        var frame = new CapturedFrame(
            32,
            24,
            128,
            pixels,
            new DesktopPixelRect(0, 0, 32, 24),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
        return new ImageCrop(frame, new CapturePixelRect(0, 0, 32, 24));
    }

    private sealed class AdaptiveStub : IOcrEngine
    {
        public string Name => "adaptive-ocr";

        public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult("安全回退", 0.95, OcrTextStatus.Recognized, "安全回退"));
    }
}
