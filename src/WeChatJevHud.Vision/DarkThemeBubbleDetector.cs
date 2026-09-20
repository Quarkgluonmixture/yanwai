using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;

namespace WeChatJevHud.Vision;

/// <summary>
/// Detects dark-theme text-bubble backgrounds with color, shape, and horizontal
/// anchoring heuristics. It deliberately ignores unbacked text and image content.
/// </summary>
public sealed class DarkThemeBubbleDetector : IBubbleDetector
{
    private const byte RemotePixel = 1;
    private const byte SelfPixel = 2;

    public IReadOnlyList<DetectedBubble> Detect(CapturedFrame frame, CapturePixelRect chatRegion)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateRegion(frame, chatRegion);

        var mask = BuildColorMask(frame, chatRegion);
        var queue = new int[mask.Length];
        var bubbles = new List<DetectedBubble>();

        for (var index = 0; index < mask.Length; index++)
        {
            var pixelType = mask[index];
            if (pixelType == 0)
            {
                continue;
            }

            var component = FloodFill(mask, chatRegion.Width, chatRegion.Height, index, pixelType, queue);
            if (TryCreateBubble(frame, component, pixelType, chatRegion, out var bubble))
            {
                bubbles.Add(bubble);
            }
        }

        return bubbles
            .OrderBy(bubble => bubble.Bounds.Y)
            .ThenBy(bubble => bubble.Bounds.X)
            .ToArray();
    }

    private static byte[] BuildColorMask(CapturedFrame frame, CapturePixelRect region)
    {
        var mask = new byte[checked(region.Width * region.Height)];
        for (var localY = 0; localY < region.Height; localY++)
        {
            var sourceRow = checked((region.Y + localY) * frame.Stride);
            var maskRow = checked(localY * region.Width);
            for (var localX = 0; localX < region.Width; localX++)
            {
                var source = checked(sourceRow + ((region.X + localX) * 4));
                var blue = frame.Bgra32Pixels[source];
                var green = frame.Bgra32Pixels[source + 1];
                var red = frame.Bgra32Pixels[source + 2];

                if (IsSelfBubbleColor(red, green, blue))
                {
                    mask[maskRow + localX] = SelfPixel;
                }
                else if (IsRemoteBubbleColor(red, green, blue))
                {
                    mask[maskRow + localX] = RemotePixel;
                }
            }
        }

        return mask;
    }

    private static bool IsSelfBubbleColor(byte red, byte green, byte blue) =>
        green >= 120 &&
        green - red >= 55 &&
        green - blue >= 25 &&
        red < 130 &&
        blue < 190;

    private static bool IsRemoteBubbleColor(byte red, byte green, byte blue)
    {
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        return red is >= 38 and <= 68 && maximum - minimum <= 5;
    }

    private static PixelComponent FloodFill(
        byte[] mask,
        int width,
        int height,
        int start,
        byte pixelType,
        int[] queue)
    {
        var head = 0;
        var tail = 0;
        queue[tail++] = start;
        mask[start] = 0;

        var startX = start % width;
        var startY = start / width;
        var minimumX = startX;
        var maximumX = startX;
        var minimumY = startY;
        var maximumY = startY;
        var pixelCount = 0;

        while (head < tail)
        {
            var current = queue[head++];
            var x = current % width;
            var y = current / width;
            pixelCount++;
            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumY = Math.Max(maximumY, y);

            if (x > 0)
            {
                Enqueue(current - 1);
            }

            if (x + 1 < width)
            {
                Enqueue(current + 1);
            }

            if (y > 0)
            {
                Enqueue(current - width);
            }

            if (y + 1 < height)
            {
                Enqueue(current + width);
            }
        }

        return new PixelComponent(
            minimumX,
            minimumY,
            maximumX - minimumX + 1,
            maximumY - minimumY + 1,
            pixelCount);

        void Enqueue(int neighbor)
        {
            if (mask[neighbor] != pixelType)
            {
                return;
            }

            mask[neighbor] = 0;
            queue[tail++] = neighbor;
        }
    }

    private static bool TryCreateBubble(
        CapturedFrame frame,
        PixelComponent component,
        byte pixelType,
        CapturePixelRect region,
        out DetectedBubble bubble)
    {
        var minimumWidth = Math.Max(12, (int)Math.Round(region.Width * 0.025));
        var minimumHeight = Math.Max(12, (int)Math.Round(region.Height * 0.018));
        var fillRatio = component.PixelCount / (double)checked(component.Width * component.Height);
        var aspectRatio = component.Width / (double)component.Height;

        if (component.Width < minimumWidth ||
            component.Height < minimumHeight ||
            component.Width > region.Width * 0.75 ||
            component.Height > region.Height * 0.28 ||
            aspectRatio < 0.70 ||
            fillRatio < 0.35)
        {
            bubble = null!;
            return false;
        }

        var bounds = new CapturePixelRect(
            region.X + component.X,
            region.Y + component.Y,
            component.Width,
            component.Height);
        if (!HasTextContrast(frame, bounds, pixelType))
        {
            bubble = null!;
            return false;
        }

        var leftGap = component.X / (double)region.Width;
        var rightGap = (region.Width - component.X - component.Width) / (double)region.Width;
        var side = pixelType switch
        {
            RemotePixel when leftGap <= 0.25 => MessageSide.Remote,
            SelfPixel when rightGap <= 0.25 => MessageSide.Self,
            _ => MessageSide.Unknown,
        };

        var anchorGap = side switch
        {
            MessageSide.Remote => leftGap,
            MessageSide.Self => rightGap,
            _ => Math.Min(leftGap, rightGap),
        };
        var shapeScore = Math.Clamp((fillRatio - 0.35) / 0.55, 0, 1);
        var alignmentScore = Math.Clamp(1 - (anchorGap / 0.28), 0, 1);
        var confidence = 0.68 + (0.20 * shapeScore) + (0.12 * alignmentScore);
        if (side == MessageSide.Unknown)
        {
            confidence = Math.Min(confidence, 0.69);
        }

        bubble = new DetectedBubble(bounds, side, Math.Clamp(confidence, 0, 0.99));
        return true;
    }

    private static bool HasTextContrast(CapturedFrame frame, CapturePixelRect bounds, byte pixelType)
    {
        var inset = Math.Max(2, (int)Math.Round(Math.Min(bounds.Width, bounds.Height) * 0.18));
        var left = bounds.X + inset;
        var top = bounds.Y + inset;
        var right = bounds.Right - inset;
        var bottom = bounds.Bottom - inset;
        if (left >= right || top >= bottom)
        {
            return false;
        }

        var contrastPixels = 0;
        var totalPixels = checked((right - left) * (bottom - top));
        for (var y = top; y < bottom; y++)
        {
            var row = checked(y * frame.Stride);
            for (var x = left; x < right; x++)
            {
                var offset = checked(row + (x * 4));
                var blue = frame.Bgra32Pixels[offset];
                var green = frame.Bgra32Pixels[offset + 1];
                var red = frame.Bgra32Pixels[offset + 2];
                var isContrast = pixelType == SelfPixel
                    ? Math.Max(red, Math.Max(green, blue)) < 100
                    : Math.Min(red, Math.Min(green, blue)) > 130;
                if (isContrast)
                {
                    contrastPixels++;
                }
            }
        }

        return contrastPixels >= Math.Max(3, (int)Math.Ceiling(totalPixels * 0.01));
    }

    private static void ValidateRegion(CapturedFrame frame, CapturePixelRect region)
    {
        if (region.IsEmpty ||
            region.X < 0 ||
            region.Y < 0 ||
            region.Right > frame.Width ||
            region.Bottom > frame.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Chat region must be inside the captured frame.");
        }
    }

    private readonly record struct PixelComponent(int X, int Y, int Width, int Height, int PixelCount);
}
