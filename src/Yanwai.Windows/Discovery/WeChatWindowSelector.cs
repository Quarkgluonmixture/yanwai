namespace Yanwai.Windows.Discovery;

public sealed class WeChatWindowSelector
{
    private static readonly HashSet<string> ProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Weixin",
        "WeChat",
    };

    public WindowCandidate? SelectBest(IEnumerable<WindowCandidate> candidates) =>
        candidates
            .Where(IsEligible)
            .OrderByDescending(Score)
            .ThenByDescending(candidate => candidate.Bounds.Area)
            .FirstOrDefault();

    private static bool IsEligible(WindowCandidate candidate) =>
        candidate.Handle != 0 &&
        ProcessNames.Contains(candidate.ProcessName) &&
        candidate.IsVisible &&
        !candidate.IsOwned &&
        !candidate.IsCloaked &&
        !candidate.Bounds.IsEmpty;

    private static int Score(WindowCandidate candidate)
    {
        var score = 0;
        score += candidate.HasRenderChild ? 30 : 0;
        score += candidate.IsMinimized ? 0 : 10;
        score += candidate.ClassName.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) ? 5 : 0;
        score += candidate.Title.Contains("WeChat", StringComparison.OrdinalIgnoreCase) ||
                 candidate.Title.Contains("Weixin", StringComparison.OrdinalIgnoreCase) ||
                 candidate.Title.Contains("微信", StringComparison.Ordinal)
            ? 5
            : 0;
        return score;
    }
}
