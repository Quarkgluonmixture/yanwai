using Yanwai.Capture;
using Yanwai.Core.Geometry;

namespace Yanwai.Ocr.Tests;

public sealed class ImageCropExtractorTests
{
    [Fact]
    public void Extract_returns_only_the_requested_capture_relative_pixels()
    {
        var frame = PixelGrid(width: 4, height: 3);
        var crop = new ImageCrop(frame, new CapturePixelRect(1, 1, 2, 1));

        var extracted = ImageCropExtractor.Extract(crop);

        Assert.Equal(2, extracted.Width);
        Assert.Equal(1, extracted.Height);
        Assert.Equal(Pixel(frame, 1, 1), Pixel(extracted, 0, 0));
        Assert.Equal(Pixel(frame, 2, 1), Pixel(extracted, 1, 0));
    }

    private static CapturedFrame PixelGrid(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * stride) + (x * 4);
                pixels[offset] = (byte)(x + 10);
                pixels[offset + 1] = (byte)(y + 20);
                pixels[offset + 2] = (byte)(x + y + 30);
                pixels[offset + 3] = 255;
            }
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

    private static (byte Blue, byte Green, byte Red, byte Alpha) Pixel(CapturedFrame frame, int x, int y)
    {
        var offset = (y * frame.Stride) + (x * 4);
        return (
            frame.Bgra32Pixels[offset],
            frame.Bgra32Pixels[offset + 1],
            frame.Bgra32Pixels[offset + 2],
            frame.Bgra32Pixels[offset + 3]);
    }
}
