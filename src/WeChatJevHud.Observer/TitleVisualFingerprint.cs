using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Observer;

/// <summary>Title ink occupancy, not a background-dominated whole-header hash. No OCR.</summary>
internal sealed record TitleVisualFingerprint(float[] Ink, double Aspect, CapturePixelRect Bounds)
{
    private const int Columns = 128;
    private const int Rows = 24;

    public static TitleVisualFingerprint? Extract(CapturedFrame frame, CapturePixelRect header)
    {
        byte Gray(int x, int y)
        {
            var i = y * frame.Stride + x * 4;
            return (byte)((frame.Bgra32Pixels[i] * 29 + frame.Bgra32Pixels[i + 1] * 150 + frame.Bgra32Pixels[i + 2] * 77) >> 8);
        }
        // Robust dark-theme background estimate. The majority of this search area is background.
        var histogram = new int[256];
        for (var y = header.Y; y < header.Bottom; y++)
            for (var x = header.X; x < header.Right; x++) histogram[Gray(x, y)]++;
        var target = header.Width * header.Height / 2;
        var background = 0;
        for (var sum = 0; background < 255; background++)
        {
            sum += histogram[background];
            if (sum >= target) break;
        }
        var left = header.Right;
        var right = header.X;
        var top = header.Bottom;
        var bottom = header.Y;
        var count = 0;
        // Pick the dominant ink row band: narrow windows can put title-bar controls
        // inside the horizontal search area, above the actual conversation title.
        var bandTop = header.Y;
        var bandBottom = header.Bottom;
        var bestMass = 0;
        var start = header.Y;
        var mass = 0;
        for (var y = header.Y; y <= header.Bottom; y++)
        {
            var rowMass = 0;
            if (y < header.Bottom)
                for (var x = header.X; x < header.Right; x++)
                    if (Gray(x, y) >= background + 35) rowMass++;
            if (rowMass > 0) { if (mass == 0) start = y; mass += rowMass; }
            else
            {
                if (mass > bestMass && y - start >= Math.Max(2, header.Height / 12))
                { bestMass = mass; bandTop = start; bandBottom = y; }
                mass = 0;
            }
        }
        if (bestMass == 0) return null;
        var textRight = header.Right;
        var lastInkX = -1;
        for (var x = header.X; x < header.Right; x++)
        {
            var hasInk = false;
            for (var y = bandTop; y < bandBottom && !hasInk; y++) hasInk = Gray(x, y) >= background + 35;
            if (hasInk) lastInkX = x;
            else if (lastInkX >= 0 && x - lastInkX > (bandBottom - bandTop) * 2)
            { textRight = lastInkX + 1; break; }
        }
        for (var y = bandTop; y < bandBottom; y++)
            for (var x = header.X; x < textRight; x++)
                if (Gray(x, y) >= background + 35)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y); count++;
                }
        if (count < 4 || right <= left || bottom <= top) return null;
        var bounds = new CapturePixelRect(left, top, right - left + 1, bottom - top + 1);
        // Area occupancy retains small glyph strokes across DPI rasterization, unlike point samples.
        var ink = new float[Columns * Rows];
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
            {
                if (Gray(x, y) < background + 35) continue;
                var x0 = (x - left) * Columns / (double)bounds.Width;
                var x1 = (x - left + 1) * Columns / (double)bounds.Width;
                var y0 = (y - top) * Rows / (double)bounds.Height;
                var y1 = (y - top + 1) * Rows / (double)bounds.Height;
                for (var row = (int)y0; row < Math.Min(Rows, Math.Ceiling(y1)); row++)
                    for (var col = (int)x0; col < Math.Min(Columns, Math.Ceiling(x1)); col++)
                        ink[row * Columns + col] += (float)((Math.Min(col + 1, x1) - Math.Max(col, x0)) * (Math.Min(row + 1, y1) - Math.Max(row, y0)));
            }
        return new(ink, bounds.Width / (double)bounds.Height, bounds);
    }

    public double Distance(TitleVisualFingerprint other)
    {
        double difference = 0, total = 0;
        var residuals = new double[Ink.Length];
        for (var i = 0; i < Ink.Length; i++)
        {
            // Sub-glyph canonical tolerance for font rasterization, not global image similarity.
            var x = i % Columns;
            var y = i / Columns;
            float nearbyThis = 0, nearbyOther = 0;
            for (var dy = Math.Max(0, y - 1); dy <= Math.Min(Rows - 1, y + 1); dy++)
                for (var dx = Math.Max(0, x - 2); dx <= Math.Min(Columns - 1, x + 2); dx++)
                {
                    nearbyThis = Math.Max(nearbyThis, Ink[dy * Columns + dx]);
                    nearbyOther = Math.Max(nearbyOther, other.Ink[dy * Columns + dx]);
                }
            residuals[i] = Math.Max(0, Ink[i] - nearbyOther) + Math.Max(0, other.Ink[i] - nearbyThis);
            difference += residuals[i];
            total += Ink[i] + other.Ink[i];
        }
        var global = difference / Math.Max(total, .001);
        // A changed character in a long title must not be diluted by unchanged characters.
        var window = Math.Clamp((int)Math.Round(Columns / Math.Max(Aspect, other.Aspect)), 8, Columns);
        var local = 0d;
        for (var start = 0; start <= Columns - window; start += Math.Max(1, window / 2))
        {
            double localDifference = 0, localInk = 0;
            for (var y = 0; y < Rows; y++)
                for (var x = start; x < start + window; x++)
                {
                    var i = y * Columns + x;
                    localDifference += residuals[i];
                    localInk += Ink[i] + other.Ink[i];
                }
            if (localInk > total * .02) local = Math.Max(local, localDifference / localInk);
        }
        return Math.Max(global, local * .8);
    }
}
