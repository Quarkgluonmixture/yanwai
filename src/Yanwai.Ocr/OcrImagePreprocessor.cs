namespace Yanwai.Ocr;

public static class OcrImagePreprocessor
{
    public static PreparedOcrImage PrepareUpscaled(ImageCrop crop, int scale = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        var source = ImageCropExtractor.Extract(crop);
        var outputWidth = checked(source.Width * scale);
        var outputHeight = checked(source.Height * scale);
        var stride = checked(outputWidth * 4);
        var pixels = new byte[checked(stride * outputHeight)];

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var sourceOffset = checked((y * source.Stride) + (x * 4));
                for (var blockY = 0; blockY < scale; blockY++)
                {
                    for (var blockX = 0; blockX < scale; blockX++)
                    {
                        var targetOffset = checked(
                            (((y * scale) + blockY) * stride) + (((x * scale) + blockX) * 4));
                        Buffer.BlockCopy(source.Bgra32Pixels, sourceOffset, pixels, targetOffset, 4);
                    }
                }
            }
        }

        return new PreparedOcrImage(outputWidth, outputHeight, stride, pixels);
    }
}

public sealed record PreparedOcrImage(int Width, int Height, int Stride, byte[] Bgra32Pixels);
