using WeChatJevHud.Capture;

namespace WeChatJevHud.Ocr;

public sealed class ScaleAwareOcrRoutingPolicy : IOcrRoutingPolicy
{
    private const int BorderInset = 4;
    private const int MinimumContrast = 42;

    public OcrRoute SelectRoute(ImageCrop crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        if (crop.Role == OcrCropRole.QuotedText ||
            crop.Bounds.IsEmpty ||
            crop.Bounds.Width < 12 ||
            crop.Bounds.Height < 12)
        {
            return OcrRoute.Adaptive;
        }

        var image = ImageCropExtractor.Extract(crop);
        var background = EstimateBackgroundLuminance(image);
        var activeRows = new bool[image.Height];
        var minimumPixels = Math.Max(2, (int)Math.Ceiling(image.Width * 0.015));

        for (var y = BorderInset; y < image.Height - BorderInset; y++)
        {
            var contrasting = 0;
            for (var x = BorderInset; x < image.Width - BorderInset; x++)
            {
                var offset = (y * image.Stride) + (x * 4);
                var luminance = Luminance(
                    image.Bgra32Pixels[offset + 2],
                    image.Bgra32Pixels[offset + 1],
                    image.Bgra32Pixels[offset]);
                if (Math.Abs(luminance - background) >= MinimumContrast)
                {
                    contrasting++;
                }
            }

            activeRows[y] = contrasting >= minimumPixels;
        }

        var bands = CountBands(activeRows, maximumBridgedGap: 2, minimumBandHeight: 3);
        return bands == 1 ? OcrRoute.PaddleSingleLine : OcrRoute.Adaptive;
    }

    private static double EstimateBackgroundLuminance(CapturedFrame image)
    {
        var samples = new List<double>();
        var edgeWidth = Math.Min(5, Math.Max(1, image.Width / 4));
        var edgeHeight = Math.Min(5, Math.Max(1, image.Height / 4));
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (x >= edgeWidth && x < image.Width - edgeWidth &&
                    y >= edgeHeight && y < image.Height - edgeHeight)
                {
                    continue;
                }

                var offset = (y * image.Stride) + (x * 4);
                samples.Add(Luminance(
                    image.Bgra32Pixels[offset + 2],
                    image.Bgra32Pixels[offset + 1],
                    image.Bgra32Pixels[offset]));
            }
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static int CountBands(bool[] rows, int maximumBridgedGap, int minimumBandHeight)
    {
        var bands = 0;
        var runStart = -1;
        var lastActive = -1;
        for (var index = 0; index <= rows.Length; index++)
        {
            if (index < rows.Length && rows[index])
            {
                runStart = runStart < 0 ? index : runStart;
                lastActive = index;
                continue;
            }

            if (runStart < 0 || (index - lastActive) <= maximumBridgedGap)
            {
                continue;
            }

            if (lastActive - runStart + 1 >= minimumBandHeight)
            {
                bands++;
            }

            runStart = -1;
            lastActive = -1;
        }

        return bands;
    }

    private static double Luminance(byte red, byte green, byte blue) =>
        (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
}
