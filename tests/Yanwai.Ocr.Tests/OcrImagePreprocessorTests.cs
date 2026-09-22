using Yanwai.Capture;
using Yanwai.Core.Geometry;

namespace Yanwai.Ocr.Tests;

public sealed class OcrImagePreprocessorTests
{
    [Fact]
    public void PrepareUpscaled_returns_coordinate_neutral_replicated_pixels()
    {
        var frame = SolidFrame(4, 3, red: 84, green: 210, blue: 139);
        PaintRectangle(frame, new CapturePixelRect(2, 1, 1, 1), red: 30, green: 31, blue: 32);

        var prepared = OcrImagePreprocessor.PrepareUpscaled(
            new ImageCrop(frame, new CapturePixelRect(1, 0, 2, 2)),
            scale: 3);

        Assert.Equal(6, prepared.Width);
        Assert.Equal(6, prepared.Height);
        Assert.Equal((byte)30, Red(prepared, 3, 3));
        Assert.Equal((byte)30, Red(prepared, 5, 5));
    }

    private static CapturedFrame SolidFrame(int width, int height, byte red, byte green, byte blue)
    {
        var frame = new CapturedFrame(
            width,
            height,
            width * 4,
            new byte[width * height * 4],
            new DesktopPixelRect(100, 200, width, height),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
        PaintRectangle(frame, new CapturePixelRect(0, 0, width, height), red, green, blue);
        return frame;
    }

    private static void PaintRectangle(
        CapturedFrame frame,
        CapturePixelRect bounds,
        byte red,
        byte green,
        byte blue)
    {
        for (var y = bounds.Y; y < bounds.Bottom; y++)
        {
            for (var x = bounds.X; x < bounds.Right; x++)
            {
                var offset = (y * frame.Stride) + (x * 4);
                frame.Bgra32Pixels[offset] = blue;
                frame.Bgra32Pixels[offset + 1] = green;
                frame.Bgra32Pixels[offset + 2] = red;
                frame.Bgra32Pixels[offset + 3] = 255;
            }
        }
    }

    private static byte Red(PreparedOcrImage image, int x, int y) =>
        image.Bgra32Pixels[(y * image.Stride) + (x * 4) + 2];
}
