using WeChatJevHud.Capture;

namespace WeChatJevHud.Ocr;

public sealed class ScaleAwareOcrRoutingPolicy : IOcrRoutingPolicy
{
    private const int MinimumContrast = 42;

    public OcrRoute SelectRoute(ImageCrop crop) => Analyze(crop).SelectedRoute;

    public OcrRoutingAnalysis Analyze(ImageCrop crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        if (crop.Role == OcrCropRole.QuotedText ||
            crop.Bounds.IsEmpty ||
            crop.Bounds.Width < 12 ||
            crop.Bounds.Height < 12)
        {
            return new(0, 0, [], [], 0, OcrRoute.Adaptive);
        }

        var image = ImageCropExtractor.Extract(crop);
        var background = EstimateBackgroundLuminance(image);
        var activeRows = new bool[image.Height];
        var rowCounts = new int[image.Height];
        var mask = new bool[image.Width * image.Height];
        var minimumPixels = Math.Max(2, (int)Math.Ceiling(image.Width * 0.015));

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var offset = (y * image.Stride) + (x * 4);
                var luminance = Luminance(
                    image.Bgra32Pixels[offset + 2],
                    image.Bgra32Pixels[offset + 1],
                    image.Bgra32Pixels[offset]);
                if (Math.Abs(luminance - background) >= MinimumContrast)
                {
                    mask[y * image.Width + x] = true;
                }
            }
        }

        // This mask is layout evidence only. OCR still receives the unchanged raw crop.
        var glyphHeights = new List<int>();
        var visited = new bool[mask.Length];
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || visited[start]) continue;
            var component = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;
            var touchesBorder = false;
            var top = image.Height;
            var bottom = 0;
            while (queue.TryDequeue(out var index))
            {
                component.Add(index);
                var x = index % image.Width;
                var y = index / image.Width;
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                touchesBorder |= x == 0 || y == 0 || x == image.Width - 1 || y == image.Height - 1;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx < 0 || nx >= image.Width || ny < 0 || ny >= image.Height) continue;
                        var neighbor = ny * image.Width + nx;
                        if (!mask[neighbor] || visited[neighbor]) continue;
                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
            }

            if (touchesBorder) continue;
            glyphHeights.Add(bottom - top + 1);
            foreach (var index in component) rowCounts[index / image.Width]++;
        }

        glyphHeights.Sort();
        // Upper quartile avoids punctuation/detached dots dominating the glyph scale.
        var glyphHeight = glyphHeights.Count == 0 ? 0 : glyphHeights[(glyphHeights.Count - 1) * 3 / 4];
        var maximumGap = Math.Max(1, (int)Math.Round(glyphHeight * 0.12));
        var minimumHeight = Math.Max(1, (int)Math.Ceiling(glyphHeight * 0.15));
        for (var y = 0; y < image.Height; y++) activeRows[y] = rowCounts[y] >= minimumPixels;
        var bands = CountBands(activeRows, maximumGap, minimumHeight);
        return new(background, minimumPixels, rowCounts, activeRows, bands,
            bands == 1 ? OcrRoute.PaddleSingleLine : OcrRoute.Adaptive,
            glyphHeight, maximumGap, minimumHeight);
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

            if (runStart < 0 || (index < rows.Length && (index - lastActive) <= maximumBridgedGap))
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

public sealed record OcrRoutingAnalysis(
    double EstimatedBackgroundLuminance,
    int MinimumContrastingPixels,
    int[] RowCounts,
    bool[] ActiveRows,
    int BandCount,
    OcrRoute SelectedRoute,
    int EstimatedGlyphHeight = 0,
    int MaximumBridgedGap = 2,
    int MinimumBandHeight = 3);
