using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yanwai.Ocr;

public static class ImageCropPngEncoder
{
    public static byte[] Encode(
        ImageCrop crop,
        OcrImagePreparation preparation = OcrImagePreparation.Raw)
    {
        var image = preparation switch
        {
            OcrImagePreparation.Raw => FromRawCrop(crop),
            OcrImagePreparation.Upscaled => OcrImagePreprocessor.PrepareUpscaled(crop),
            _ => throw new ArgumentOutOfRangeException(nameof(preparation)),
        };
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        var source = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            image.Bgra32Pixels,
            image.Stride);
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(output);
        return output.ToArray();
    }

    private static PreparedOcrImage FromRawCrop(ImageCrop crop)
    {
        var frame = ImageCropExtractor.Extract(crop);
        return new PreparedOcrImage(frame.Width, frame.Height, frame.Stride, frame.Bgra32Pixels);
    }
}

public enum OcrImagePreparation
{
    Raw,
    Upscaled,
}
