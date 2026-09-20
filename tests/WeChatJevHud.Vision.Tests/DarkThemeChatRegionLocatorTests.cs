using WeChatJevHud.Capture;

namespace WeChatJevHud.Vision.Tests;

public sealed class DarkThemeChatRegionLocatorTests
{
    [Fact]
    public void Locate_finds_the_chat_content_between_the_header_composer_and_conversation_list()
    {
        var frame = PngFrameReader.Load(FixturePath("wechat-dark-layout-reference.png"));

        var region = new DarkThemeChatRegionLocator().Locate(frame);

        Assert.InRange(region.Bounds.X, 375, 385);
        Assert.InRange(region.Bounds.Y, 116, 124);
        Assert.InRange(region.Bounds.Right, frame.Width - 1, frame.Width);
        Assert.InRange(region.Bounds.Bottom, 1384, 1392);
        Assert.True(region.Confidence >= 0.8, $"ROI confidence was {region.Confidence:F3}");
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
