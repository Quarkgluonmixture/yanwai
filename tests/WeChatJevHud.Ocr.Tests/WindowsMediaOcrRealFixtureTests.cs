using System.IO;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Ocr.Tests;

public sealed class WindowsMediaOcrRealFixtureTests
{
    [Fact]
    public async Task RecognizeAsync_reads_a_short_simplified_chinese_bubble_crop()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "wechat-dark-layout-reference.png");
        var frame = PngFrameReader.Load(fixturePath);
        var engine = new WindowsMediaOcrEngine("zh-Hans-CN");

        var result = await engine.RecognizeAsync(
            new ImageCrop(frame, new CapturePixelRect(478, 939, 127, 54)),
            CancellationToken.None);

        Assert.Equal(OcrTextStatus.Recognized, result.Status);
        Assert.Equal("诶哟我去", result.Text);
        Assert.Equal(result.Text, OcrTextNormalizer.Normalize(result.RawText));
        Assert.NotEmpty(result.RawText);
        Assert.Null(result.OcrConfidence);
    }
}
