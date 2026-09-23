using System.IO;
using Yanwai.Capture;
using Yanwai.Core.Geometry;

namespace Yanwai.Ocr.Tests;

public sealed class TextLineSplitterTests
{
    private static CapturedFrame Crop(int x, int y, int width, int height)
    {
        var frame = PngFrameReader.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "wechat-dark-layout-reference.png"));
        return ImageCropExtractor.Extract(new ImageCrop(frame, new CapturePixelRect(x, y, width, height)));
    }

    /// <summary>A whole two-line crop truncates the second line in a line recognizer.</summary>
    [Fact]
    public void A_wrapped_quote_splits_into_two_lines_top_to_bottom()
    {
        var lines = TextLineSplitter.Split(Crop(486, 1004, 455, 54));

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Bottom <= lines[1].Y + 2);
    }

    /// <summary>
    /// The bubble's tail and corner sit left of the text; left in the crop they made the
    /// recognizer drop the first character. The line must start at the glyphs.
    /// </summary>
    [Fact]
    public void A_short_bubble_is_one_tight_line_that_leaves_the_tail_out()
    {
        var bubble = Crop(478, 939, 127, 54);

        var line = Assert.Single(TextLineSplitter.Split(bubble));

        Assert.True(line.X >= 12, $"line starts at x={line.X}, inside the tail");
        Assert.True(line.Height < bubble.Height, "line is tighter than the bubble");
    }
}
