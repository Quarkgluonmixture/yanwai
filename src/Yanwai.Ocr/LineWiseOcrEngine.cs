using Yanwai.Capture;
using Yanwai.Core.Geometry;

namespace Yanwai.Ocr;

/// <summary>
/// Feeds another engine one line at a time, each line cropped tight, given a margin and
/// turned into dark text on a light background.
///
/// Windows OCR returned nothing at all for a white-on-dark remote bubble at 175% scale
/// that PP-OCR read cleanly; as the checker in <see cref="CrossCheckedOcrEngine"/> that
/// silence vetoed a correct reading. Printed-page engines expect ink on paper.
/// </summary>
public sealed class LineWiseOcrEngine : IOcrEngine
{
    private readonly IOcrEngine _inner;

    public LineWiseOcrEngine(IOcrEngine inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string Name => $"line-wise:{_inner.Name}";

    public async Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
    {
        var bubble = ImageCropExtractor.Extract(crop);
        var fill = TextLineSplitter.DominantColor(bubble);
        var invert = (0.114 * fill.B) + (0.587 * fill.G) + (0.299 * fill.R) < 128;

        var texts = new List<string>();
        var raws = new List<string>();
        foreach (var line in TextLineSplitter.Split(bubble))
        {
            var image = LineImage(bubble, line, fill, invert);
            var result = await _inner.RecognizeAsync(
                new ImageCrop(image, new CapturePixelRect(0, 0, image.Width, image.Height)),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(result.Text))
            {
                texts.Add(result.Text);
                raws.Add(result.RawText);
            }
        }

        var text = OcrTextNormalizer.Normalize(string.Concat(texts));
        return text.Length == 0
            ? new OcrResult(string.Empty, null, OcrTextStatus.NoText, string.Join('\n', raws))
            : new OcrResult(text, null, OcrTextStatus.Recognized, string.Join('\n', raws));
    }

    /// <summary>The line on a margin of its own fill, inverted when the fill is dark.</summary>
    private static CapturedFrame LineImage(CapturedFrame bubble, CapturePixelRect line, (int B, int G, int R) fill, bool invert)
    {
        var margin = Math.Max(4, line.Height / 3);
        var width = line.Width + (2 * margin);
        var height = line.Height + (2 * margin);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = x - margin;
                var sourceY = y - margin;
                int b, g, r;
                if (sourceX >= 0 && sourceY >= 0 && sourceX < line.Width && sourceY < line.Height)
                {
                    var offset = ((line.Y + sourceY) * bubble.Stride) + ((line.X + sourceX) * 4);
                    (b, g, r) = (bubble.Bgra32Pixels[offset], bubble.Bgra32Pixels[offset + 1], bubble.Bgra32Pixels[offset + 2]);
                }
                else
                {
                    (b, g, r) = fill;
                }

                var target = (y * stride) + (x * 4);
                pixels[target] = (byte)(invert ? 255 - b : b);
                pixels[target + 1] = (byte)(invert ? 255 - g : g);
                pixels[target + 2] = (byte)(invert ? 255 - r : r);
                pixels[target + 3] = 255;
            }
        }

        return new CapturedFrame(
            width, height, stride, pixels, bubble.DesktopBounds, bubble.Method, bubble.CapturedAt, bubble.Duration);
    }
}
