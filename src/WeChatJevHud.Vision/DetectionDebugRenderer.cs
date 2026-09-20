using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;

namespace WeChatJevHud.Vision;

public static class DetectionDebugRenderer
{
    public static CapturedFrame Render(CapturedFrame frame, BubbleDetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(result);

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(PngFrameWriter.ToBitmapSource(frame), new Rect(0, 0, frame.Width, frame.Height));
            var thickness = Math.Max(2, frame.Width / 600d);
            drawing.DrawRectangle(
                null,
                new Pen(Brushes.DeepSkyBlue, thickness),
                ToRect(result.ChatRegion.Bounds));
            DrawLabel(
                drawing,
                $"chat ROI {result.ChatRegion.Confidence:F2}",
                result.ChatRegion.Bounds.X + thickness,
                result.ChatRegion.Bounds.Y + thickness,
                Brushes.DeepSkyBlue,
                frame);

            foreach (var bubble in result.Bubbles)
            {
                var color = bubble.Side switch
                {
                    MessageSide.Remote => Brushes.Orange,
                    MessageSide.Self => Brushes.LimeGreen,
                    _ => Brushes.Magenta,
                };
                drawing.DrawRectangle(null, new Pen(color, thickness), ToRect(bubble.Bounds));
                var label = FormattableString.Invariant(
                    $"{bubble.Side} {bubble.Confidence:F2} [{bubble.Bounds.X},{bubble.Bounds.Y},{bubble.Bounds.Width},{bubble.Bounds.Height}]");
                DrawLabel(drawing, label, bubble.Bounds.X, bubble.Bounds.Y, color, frame);
            }
        }

        var rendered = new RenderTargetBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var stride = checked(frame.Width * 4);
        var pixels = new byte[checked(stride * frame.Height)];
        rendered.CopyPixels(pixels, stride, 0);
        return new CapturedFrame(
            frame.Width,
            frame.Height,
            stride,
            pixels,
            frame.DesktopBounds,
            frame.Method,
            DateTimeOffset.UtcNow,
            result.Duration);
    }

    private static void DrawLabel(
        DrawingContext drawing,
        string text,
        double x,
        double y,
        Brush color,
        CapturedFrame frame)
    {
        var fontSize = Math.Max(12, frame.Height / 100d);
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            Brushes.White,
            1);
        var labelX = Math.Clamp(x, 0, Math.Max(0, frame.Width - formatted.Width - 8));
        var labelY = Math.Clamp(y - formatted.Height - 2, 0, Math.Max(0, frame.Height - formatted.Height - 4));
        drawing.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(210, 20, 20, 20)),
            null,
            new Rect(labelX, labelY, formatted.Width + 8, formatted.Height + 4));
        drawing.DrawText(formatted, new Point(labelX + 4, labelY + 2));
        drawing.DrawLine(new Pen(color, 2), new Point(labelX, labelY), new Point(labelX + formatted.Width + 8, labelY));
    }

    private static Rect ToRect(CapturePixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
