using Yanwai.Capture;
using Yanwai.Core.Geometry;

namespace Yanwai.Vision;

/// <summary>
/// Locates the dark-theme chat content from structural dividers. All returned
/// coordinates are relative to the captured render frame.
/// </summary>
public sealed class DarkThemeChatRegionLocator : IChatRegionLocator
{
    private const int EdgeDifference = 4;
    private const double StrongVerticalDivider = 0.70;

    public DetectedChatRegion Locate(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var verticalSearchEnd = Math.Max(2, (int)Math.Round(frame.Width * 0.48));
        var paneLeft = 0;
        var paneDividerScore = 0d;
        for (var x = 1; x < verticalSearchEnd; x++)
        {
            var score = VerticalEdgeScore(frame, x);
            if (score >= StrongVerticalDivider)
            {
                paneLeft = x;
                paneDividerScore = score;
            }
        }

        var provisionalLeft = paneLeft > 0
            ? paneLeft
            : Math.Clamp((int)Math.Round(frame.Width * 0.15), 0, frame.Width - 1);
        var header = FindHorizontalDivider(frame, provisionalLeft, 0.04, 0.20);

        // Compact WeChat hides the conversation list and leaves no vertical
        // divider. In that layout, the navigation rail tracks the header height.
        if (paneLeft == 0)
        {
            paneLeft = Math.Clamp((int)Math.Round(header.Position * 0.80), 0, frame.Width - 1);
            paneDividerScore = 0.75;
            header = FindHorizontalDivider(frame, paneLeft, 0.04, 0.20);
        }

        var composer = FindHorizontalDivider(frame, paneLeft, 0.70, 0.96);
        var contentTop = checked(header.Position + 1);
        var bounds = new CapturePixelRect(
            paneLeft,
            contentTop,
            frame.Width - paneLeft,
            composer.Position - contentTop);
        if (bounds.IsEmpty)
        {
            throw new InvalidOperationException("The WeChat chat region dividers produced an empty rectangle.");
        }

        var detectionScore = Math.Clamp(
            (paneDividerScore + header.Score + composer.Score) / 3d,
            0,
            1);
        return new DetectedChatRegion(bounds, detectionScore);
    }

    private static Divider FindHorizontalDivider(
        CapturedFrame frame,
        int left,
        double startRatio,
        double endRatio)
    {
        var start = Math.Clamp((int)Math.Round(frame.Height * startRatio), 1, frame.Height - 1);
        var end = Math.Clamp((int)Math.Round(frame.Height * endRatio), start + 1, frame.Height);
        var best = new Divider(start, 0);
        for (var y = start; y < end; y++)
        {
            var score = HorizontalEdgeScore(frame, y, left);
            if (score > best.Score)
            {
                best = new Divider(y, score);
            }
        }

        return best;
    }

    private static double VerticalEdgeScore(CapturedFrame frame, int x)
    {
        var changed = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            if (MaximumChannelDifference(frame, x - 1, y, x, y) > EdgeDifference)
            {
                changed++;
            }
        }

        return changed / (double)frame.Height;
    }

    private static double HorizontalEdgeScore(CapturedFrame frame, int y, int left)
    {
        var changed = 0;
        var width = frame.Width - left;
        for (var x = left; x < frame.Width; x++)
        {
            if (MaximumChannelDifference(frame, x, y - 1, x, y) > EdgeDifference)
            {
                changed++;
            }
        }

        return changed / (double)width;
    }

    private static int MaximumChannelDifference(
        CapturedFrame frame,
        int firstX,
        int firstY,
        int secondX,
        int secondY)
    {
        var first = checked((firstY * frame.Stride) + (firstX * 4));
        var second = checked((secondY * frame.Stride) + (secondX * 4));
        var blue = Math.Abs(frame.Bgra32Pixels[first] - frame.Bgra32Pixels[second]);
        var green = Math.Abs(frame.Bgra32Pixels[first + 1] - frame.Bgra32Pixels[second + 1]);
        var red = Math.Abs(frame.Bgra32Pixels[first + 2] - frame.Bgra32Pixels[second + 2]);
        return Math.Max(red, Math.Max(green, blue));
    }

    private readonly record struct Divider(int Position, double Score);
}
