using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Overlay;

/// <summary>Interface only; the anchored HUD implementation belongs to Phase 6.</summary>
public interface IOverlayPresenter
{
    void Show(OverlayContent content, DesktopPixelRect anchor);

    void Hide();
}

public sealed record OverlayContent(IReadOnlyList<string> Rows);
