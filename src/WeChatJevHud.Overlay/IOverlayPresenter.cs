using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Overlay;

/// <summary>
/// Shows judgments beside the message they are about. Implementations own a window
/// that is independent of WeChat and must never be given focus.
/// </summary>
public interface IOverlayPresenter
{
    /// <summary>
    /// Attaches the HUD to a bubble. The anchor is in capture-frame pixels and is
    /// re-projected on every <see cref="Follow"/> call, so the window may move or
    /// resize afterwards without a new Show.
    /// </summary>
    void Show(OverlayContent content, CapturePixelRect anchor, CapturePixelRect chatRegion);

    /// <summary>
    /// Re-places the HUD against the current window state. A null snapshot, or a
    /// hidden or minimized window, hides the HUD rather than leaving it floating over
    /// whatever is now in front.
    /// </summary>
    void Follow(WeChatWindowSnapshot? snapshot);

    void Hide();
}

/// <summary>
/// What the collapsed HUD shows: a headline verdict, a suggested next step, and a few
/// rows. Long lists belong in the panel, not here.
/// </summary>
public sealed record OverlayContent(
    string Message,
    string Headline,
    string Advice,
    IReadOnlyList<OverlayRow> Rows);

public sealed record OverlayRow(string Label, double Probability)
{
    public string ProbabilityText => $"{Probability * 100:N0}%";
}
