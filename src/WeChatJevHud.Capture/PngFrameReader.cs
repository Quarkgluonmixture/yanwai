using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Capture;

public static class PngFrameReader
{
    public static CapturedFrame Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);

        return new CapturedFrame(
            converted.PixelWidth,
            converted.PixelHeight,
            stride,
            pixels,
            new DesktopPixelRect(0, 0, converted.PixelWidth, converted.PixelHeight),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
    }
}
