using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Capture;

public sealed class CapturedFrame
{
    public CapturedFrame(
        int width,
        int height,
        int stride,
        byte[] bgra32Pixels,
        DesktopPixelRect desktopBounds,
        CaptureMethod method,
        DateTimeOffset capturedAt,
        TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, checked(width * 4));
        ArgumentNullException.ThrowIfNull(bgra32Pixels);
        if (bgra32Pixels.Length != checked(stride * height))
        {
            throw new ArgumentException("Pixel buffer length must equal stride multiplied by height.", nameof(bgra32Pixels));
        }

        Width = width;
        Height = height;
        Stride = stride;
        Bgra32Pixels = bgra32Pixels;
        DesktopBounds = desktopBounds;
        Method = method;
        CapturedAt = capturedAt;
        Duration = duration;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Bgra32Pixels { get; }

    public DesktopPixelRect DesktopBounds { get; }

    public CaptureMethod Method { get; }

    public DateTimeOffset CapturedAt { get; }

    public TimeSpan Duration { get; }
}

public enum CaptureMethod
{
    RenderWindow,
    VisibleDesktopFallback,
}
