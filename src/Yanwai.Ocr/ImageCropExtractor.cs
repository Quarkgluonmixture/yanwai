using Yanwai.Capture;
using Yanwai.Core.Geometry;

namespace Yanwai.Ocr;

public static class ImageCropExtractor
{
    public static CapturedFrame Extract(ImageCrop crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        ArgumentNullException.ThrowIfNull(crop.Frame);

        var bounds = crop.Bounds;
        var frame = crop.Frame;
        if (bounds.IsEmpty ||
            bounds.X < 0 ||
            bounds.Y < 0 ||
            bounds.Right > frame.Width ||
            bounds.Bottom > frame.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(crop), "Crop bounds must be non-empty and inside the captured frame.");
        }

        var stride = checked(bounds.Width * 4);
        var pixels = new byte[checked(stride * bounds.Height)];
        for (var y = 0; y < bounds.Height; y++)
        {
            var sourceOffset = checked(((bounds.Y + y) * frame.Stride) + (bounds.X * 4));
            Buffer.BlockCopy(frame.Bgra32Pixels, sourceOffset, pixels, y * stride, stride);
        }

        return new CapturedFrame(
            bounds.Width,
            bounds.Height,
            stride,
            pixels,
            new DesktopPixelRect(
                frame.DesktopBounds.X + bounds.X,
                frame.DesktopBounds.Y + bounds.Y,
                bounds.Width,
                bounds.Height),
            frame.Method,
            frame.CapturedAt,
            frame.Duration);
    }
}
