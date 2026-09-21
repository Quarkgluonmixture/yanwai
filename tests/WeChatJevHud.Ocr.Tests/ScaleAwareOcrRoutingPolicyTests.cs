using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using Xunit;

namespace WeChatJevHud.Ocr.Tests;

public sealed class ScaleAwareOcrRoutingPolicyTests
{
    [Fact]
    public void RoutesOneTextBandToPaddle()
    {
        var crop = SyntheticCrop(160, 54, [(17, 31)]);

        Assert.Equal(OcrRoute.PaddleSingleLine, new ScaleAwareOcrRoutingPolicy().SelectRoute(crop));
    }

    [Fact]
    public void RoutesWrappedTextBandsToAdaptive()
    {
        var crop = SyntheticCrop(260, 64, [(9, 22), (35, 49)]);

        Assert.Equal(OcrRoute.Adaptive, new ScaleAwareOcrRoutingPolicy().SelectRoute(crop));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void BorderConnectedBubbleCornersDoNotBecomeTextBands(double scale)
    {
        var width = (int)(156 * scale);
        var height = (int)(36 * scale);
        var crop = SyntheticCrop(width, height, [((int)(13 * scale), (int)(23 * scale))]);
        var pixels = crop.Frame.Bgra32Pixels;
        for (var y = 0; y < height; y++)
        {
            var edge = y < 7 * scale || y >= height - 7 * scale ? (int)(6 * scale) : (int)(4 * scale);
            for (var x = width - edge; x < width; x++)
            {
                var offset = y * width * 4 + x * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 230;
            }
        }

        Assert.Equal(OcrRoute.PaddleSingleLine, new ScaleAwareOcrRoutingPolicy().SelectRoute(crop));
    }

    [Fact]
    public void RoutesSingleLineQuotedTextToAdaptive()
    {
        var main = SyntheticCrop(200, 54, [(17, 31)]);
        var quoted = main with { Role = OcrCropRole.QuotedText };

        Assert.Equal(OcrRoute.Adaptive, new ScaleAwareOcrRoutingPolicy().SelectRoute(quoted));
    }

    [Fact]
    public void FinalTextBandWithOnePixelBottomPaddingIsNotDropped()
    {
        var crop = SyntheticCrop(240, 60, [(10, 25), (40, 58)]);
        Assert.Equal(OcrRoute.Adaptive, new ScaleAwareOcrRoutingPolicy().SelectRoute(crop));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void MultilineRemainsAdaptiveAndRoutingDoesNotModifyOcrPixels(double scale)
    {
        var crop = SyntheticCrop((int)(240 * scale), (int)(60 * scale),
            [((int)(10 * scale), (int)(22 * scale)), ((int)(35 * scale), (int)(47 * scale))]);
        var before = crop.Frame.Bgra32Pixels.ToArray();

        var analysis = new ScaleAwareOcrRoutingPolicy().Analyze(crop);

        Assert.Equal(OcrRoute.Adaptive, analysis.SelectedRoute);
        Assert.Equal(2, analysis.BandCount);
        Assert.Equal(before, crop.Frame.Bgra32Pixels);
    }

    [Theory]
    [InlineData(0.67)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void RoutingIsStableAcrossRenderScales(double scale)
    {
        var height = (int)Math.Round(54 * scale);
        var width = (int)Math.Round(160 * scale);
        var start = (int)Math.Round(17 * scale);
        var end = (int)Math.Round(31 * scale);

        Assert.Equal(
            OcrRoute.PaddleSingleLine,
            new ScaleAwareOcrRoutingPolicy().SelectRoute(SyntheticCrop(width, height, [(start, end)])));
    }

    private static ImageCrop SyntheticCrop(int width, int height, IReadOnlyList<(int Start, int End)> bands)
    {
        const byte background = 70;
        const byte foreground = 230;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var active = bands.Any(band => y >= band.Start && y <= band.End) &&
                             x >= Math.Max(6, width / 10) && x < width - Math.Max(6, width / 10);
                var value = active ? foreground : background;
                var offset = (y * stride) + (x * 4);
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        var frame = new CapturedFrame(
            width,
            height,
            stride,
            pixels,
            new DesktopPixelRect(0, 0, width, height),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
        return new ImageCrop(frame, new CapturePixelRect(0, 0, width, height));
    }
}
