using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WeChatJevHud.Capture;

public static class PngFrameWriter
{
    public static void Save(CapturedFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var output = File.Create(fullPath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource(frame)));
        encoder.Save(output);
    }

    public static BitmapSource ToBitmapSource(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var source = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.Bgra32Pixels,
            frame.Stride);
        source.Freeze();
        return source;
    }
}
