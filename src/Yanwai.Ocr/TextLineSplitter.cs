using Yanwai.Capture;
using Yanwai.Core.Geometry;

namespace Yanwai.Ocr;

/// <summary>
/// Finds the text lines inside one bubble crop and returns each as a tight box.
///
/// Line recognizers such as PP-OCR are trained on detector output: one line, cropped
/// close to the glyphs. A whole bubble breaks both assumptions. A second line is
/// silently truncated, and the bubble's rounded corner and tail at the left edge made
/// the model drop the first character with full confidence — both measured on the
/// public reference fixture.
///
/// Works for either theme: "ink" is whatever differs strongly from the bubble's own
/// fill, so light-on-dark and dark-on-green bubbles both split.
/// </summary>
public static class TextLineSplitter
{
    /// <summary>Per-channel difference from the fill that counts as a glyph pixel.</summary>
    private const int InkThreshold = 60;

    /// <summary>More blank rows than this separate two lines.</summary>
    private const int MaximumGapInsideLine = 2;

    /// <summary>Runs shorter than this are specks, not text.</summary>
    private const int MinimumLineHeight = 5;

    /// <summary>Margin around each line, as a fraction of its height.</summary>
    private const double MarginRatio = 0.15;

    public static IReadOnlyList<CapturePixelRect> Split(CapturedFrame crop)
    {
        ArgumentNullException.ThrowIfNull(crop);

        var width = crop.Width;
        var height = crop.Height;
        if (width < 8 || height < 8)
        {
            return [];
        }

        var fill = DominantColor(crop);
        var ink = new bool[height, width];

        // The frame of the crop is bubble outline, corner and tail, never text.
        var left = Math.Max(3, width / 40);
        for (var y = 3; y < height - 3; y++)
        {
            var row = y * crop.Stride;
            for (var x = left; x < width - 3; x++)
            {
                var offset = row + (x * 4);
                var difference = Math.Max(
                    Math.Abs(crop.Bgra32Pixels[offset] - fill.B),
                    Math.Max(
                        Math.Abs(crop.Bgra32Pixels[offset + 1] - fill.G),
                        Math.Abs(crop.Bgra32Pixels[offset + 2] - fill.R)));
                ink[y, x] = difference > InkThreshold;
            }
        }

        var lines = new List<CapturePixelRect>();
        var runStart = -1;
        var lastInkRow = -1;
        for (var y = 0; y < height; y++)
        {
            if (!RowHasInk(ink, y, width))
            {
                continue;
            }

            if (runStart < 0)
            {
                runStart = y;
            }
            else if (y - lastInkRow - 1 > MaximumGapInsideLine)
            {
                AddLine(runStart, lastInkRow);
                runStart = y;
            }

            lastInkRow = y;
        }

        if (runStart >= 0)
        {
            AddLine(runStart, lastInkRow);
        }

        return lines;

        void AddLine(int top, int bottom)
        {
            var lineHeight = bottom - top + 1;
            if (lineHeight < MinimumLineHeight)
            {
                return;
            }

            var first = -1;
            var last = -1;
            for (var x = 0; x < width; x++)
            {
                for (var y = top; y <= bottom; y++)
                {
                    if (ink[y, x])
                    {
                        if (first < 0)
                        {
                            first = x;
                        }

                        last = x;
                        break;
                    }
                }
            }

            var margin = Math.Max(2, (int)(lineHeight * MarginRatio));
            var x0 = Math.Max(0, first - margin);
            var y0 = Math.Max(0, top - margin);
            var x1 = Math.Min(width, last + margin + 1);
            var y1 = Math.Min(height, bottom + margin + 1);
            lines.Add(new CapturePixelRect(x0, y0, x1 - x0, y1 - y0));
        }
    }

    private static bool RowHasInk(bool[,] ink, int y, int width)
    {
        for (var x = 0; x < width; x++)
        {
            if (ink[y, x])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The most common colour in the middle half of the crop: the bubble fill.</summary>
    public static (int B, int G, int R) DominantColor(CapturedFrame crop)
    {
        var counts = new Dictionary<int, int>();
        for (var y = crop.Height / 4; y < crop.Height * 3 / 4; y++)
        {
            for (var x = crop.Width / 4; x < crop.Width * 3 / 4; x++)
            {
                var offset = (y * crop.Stride) + (x * 4);
                var key = crop.Bgra32Pixels[offset]
                          | (crop.Bgra32Pixels[offset + 1] << 8)
                          | (crop.Bgra32Pixels[offset + 2] << 16);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        var dominant = counts.MaxBy(pair => pair.Value).Key;
        return (dominant & 0xFF, (dominant >> 8) & 0xFF, (dominant >> 16) & 0xFF);
    }
}
