using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Ocr.Tests;

public sealed class WindowsMediaOcrEngineTests
{
    [Fact]
    public async Task RecognizeAsync_returns_no_text_and_no_invented_confidence_for_a_blank_crop()
    {
        var frame = WhiteFrame(width: 80, height: 40);
        var engine = new WindowsMediaOcrEngine("zh-Hans-CN");

        var result = await engine.RecognizeAsync(
            new ImageCrop(frame, new CapturePixelRect(10, 10, 50, 20)),
            CancellationToken.None);

        Assert.Equal(OcrTextStatus.NoText, result.Status);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(string.Empty, result.RawText);
        Assert.Null(result.OcrConfidence);
    }

    [Fact]
    public async Task RecognizeAsync_recognizes_an_english_text_crop()
    {
        var frame = TextFrame("Hello OCR 123", width: 280, height: 64);
        var engine = new WindowsMediaOcrEngine("en-US", OcrImagePreparation.Upscaled);

        var result = await engine.RecognizeAsync(
            new ImageCrop(frame, new CapturePixelRect(0, 0, frame.Width, frame.Height)),
            CancellationToken.None);

        Assert.Equal(OcrTextStatus.Recognized, result.Status);
        Assert.Contains("Hello OCR 123", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello OCR 123", result.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.OcrConfidence);
    }

    private static CapturedFrame WhiteFrame(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 255;
            pixels[offset + 1] = 255;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = 255;
        }

        return new CapturedFrame(
            width,
            height,
            stride,
            pixels,
            new DesktopPixelRect(0, 0, width, height),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
    }

    private static CapturedFrame TextFrame(string text, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var formatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                30,
                Brushes.Black,
                1);
            drawing.DrawText(formatted, new Point(8, 8));
        }

        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        rendered.CopyPixels(pixels, stride, 0);
        return new CapturedFrame(
            width,
            height,
            stride,
            pixels,
            new DesktopPixelRect(0, 0, width, height),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
    }
}
