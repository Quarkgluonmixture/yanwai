using Yanwai.Core.Geometry;
using Yanwai.Windows.Discovery;

namespace Yanwai.Windows.Tests;

public sealed class WeChatWindowSelectorTests
{
    [Fact]
    public void SelectBest_prefers_the_visible_rendering_window_over_hidden_helpers()
    {
        var hiddenHelper = new WindowCandidate(
            Handle: 10,
            ProcessId: 100,
            ProcessName: "Weixin",
            Title: string.Empty,
            ClassName: "Qt51514QWindowToolSaveBits",
            Bounds: new DesktopPixelRect(0, 0, 200, 100),
            IsVisible: false,
            IsMinimized: false,
            IsOwned: true,
            IsCloaked: false,
            HasRenderChild: false);

        var conversationWindow = new WindowCandidate(
            Handle: 20,
            ProcessId: 100,
            ProcessName: "Weixin",
            Title: "WeChat",
            ClassName: "Qt51514QWindowIcon",
            Bounds: new DesktopPixelRect(-1200, 80, 1100, 760),
            IsVisible: true,
            IsMinimized: false,
            IsOwned: false,
            IsCloaked: false,
            HasRenderChild: true);

        var selected = new WeChatWindowSelector().SelectBest([hiddenHelper, conversationWindow]);

        Assert.NotNull(selected);
        Assert.Equal((nint)20, selected.Handle);
    }

    [Fact]
    public void SelectBest_does_not_match_process_names_that_only_contain_Weixin()
    {
        var unrelated = new WindowCandidate(
            Handle: 30,
            ProcessId: 200,
            ProcessName: "WeixinInstaller",
            Title: "Installer",
            ClassName: "Window",
            Bounds: new DesktopPixelRect(0, 0, 1200, 800),
            IsVisible: true,
            IsMinimized: false,
            IsOwned: false,
            IsCloaked: false,
            HasRenderChild: false);

        Assert.Null(new WeChatWindowSelector().SelectBest([unrelated]));
    }

    [Fact]
    public void SelectBest_ignores_hidden_and_owned_helper_windows()
    {
        var hidden = Candidate(handle: 40, isVisible: false, isOwned: false);
        var ownedPopup = Candidate(handle: 50, isVisible: true, isOwned: true);

        Assert.Null(new WeChatWindowSelector().SelectBest([hidden, ownedPopup]));
    }

    private static WindowCandidate Candidate(nint handle, bool isVisible, bool isOwned) =>
        new(
            Handle: handle,
            ProcessId: 300,
            ProcessName: "Weixin",
            Title: "WeChat helper",
            ClassName: "Qt51514QWindowIcon",
            Bounds: new DesktopPixelRect(0, 0, 900, 700),
            IsVisible: isVisible,
            IsMinimized: false,
            IsOwned: isOwned,
            IsCloaked: false,
            HasRenderChild: false);
}
