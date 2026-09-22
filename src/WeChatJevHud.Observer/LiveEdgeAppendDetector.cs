using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;

namespace WeChatJevHud.Observer;

public sealed record LiveEdgeBubble(MessageSide Side, CapturePixelRect Bounds, string? CropFingerprint, bool IsFullyVisible);

public sealed record LiveEdgeAppendDecision(string Reason, int PreviousStart, int SuffixStart, double DeltaY, int CurrentStart = 0)
{
    public bool IsAppend => SuffixStart >= 0;
    public static LiveEdgeAppendDecision Suppressed(string reason) => new(reason, 0, -1, 0);
}

/// <summary>
/// Recognizes an ordered live-sequence extension, never arbitrary history overlap.
/// No OCR, LCS, text-specific rules or recent-history buffer participates.
/// </summary>
public sealed class LiveEdgeAppendDetector
{
    public LiveEdgeAppendDecision Detect(
        IReadOnlyList<LiveEdgeBubble> previous,
        IReadOnlyList<LiveEdgeBubble> current,
        CapturePixelRect viewport,
        bool atLiveEdge,
        bool stable,
        int boundaryMargin = 0)
    {
        if (!stable || !atLiveEdge) return LiveEdgeAppendDecision.Suppressed("not_stable_live_edge");
        var currentStart = 0;
        while (currentStart < current.Count && !current[currentStart].IsFullyVisible && current[currentStart].Bounds.Y <= viewport.Y + boundaryMargin)
            currentStart++;
        if (current.Count == currentStart || current.Skip(currentStart).Any(b => !b.IsFullyVisible))
            return LiveEdgeAppendDecision.Suppressed("empty_or_partial_view");
        if (previous.Count == 0)
            return currentStart == 0 ? new("established_empty_edge", 0, 0, 0) : LiveEdgeAppendDecision.Suppressed("partial_empty_edge");

        // Pair by chronological occurrence, not a best-fit LCS that can steal a repeat.
        for (var start = 0; start < previous.Count; start++)
        {
            var retained = previous.Count - start;
            if (current.Count <= retained + currentStart) continue;
            if (!Enumerable.Range(0, retained).All(i => Same(previous[start + i], current[currentStart + i]))) continue;
            var delta = current[currentStart].Bounds.Y - previous[start].Bounds.Y;
            var tolerance = Math.Max(1, previous[start].Bounds.Height * .05);
            if (!Enumerable.Range(0, retained).All(i =>
                    Math.Abs(current[currentStart + i].Bounds.Y - previous[start + i].Bounds.Y - delta) <= tolerance &&
                    current[currentStart + i].Bounds.X == previous[start + i].Bounds.X)) continue;
            var suffix = currentStart + retained;
            if (current[suffix].Bounds.Y < current[suffix - 1].Bounds.Bottom) continue;

            if (start == 0 && currentStart == 0 && Math.Abs(delta) <= tolerance)
                return new("stationary_prefix_suffix", start, retained, delta);

            // A translated append needs a non-ambiguous anchor. Disappearing older
            // prefix occurrences must have actually left the viewport, not be skipped.
            var uniqueAnchor = Enumerable.Range(0, retained).Any(i =>
                previous.Count(b => Same(b, current[currentStart + i])) == 1 &&
                current.Count(b => Same(b, current[currentStart + i])) == 1);
            if (!uniqueAnchor || delta >= -tolerance) continue;
            var clippedPrefix = previous.Take(start).Where(b => b.Bounds.Bottom + delta > viewport.Y).ToArray();
            if (clippedPrefix.Length != currentStart) continue;
            if (!Enumerable.Range(0, currentStart).All(i =>
                    clippedPrefix[i].Bounds.Y + delta <= viewport.Y + boundaryMargin &&
                    clippedPrefix[i].Side == current[i].Side &&
                    clippedPrefix[i].Bounds.X == current[i].Bounds.X &&
                    clippedPrefix[i].Bounds.Width == current[i].Bounds.Width &&
                    Math.Abs(clippedPrefix[i].Bounds.Bottom + delta - current[i].Bounds.Bottom) <= tolerance)) continue;
            if (current[^1].Bounds.Bottom < previous[^1].Bounds.Bottom - tolerance) continue;
            return new("anchored_translated_suffix", start, suffix, delta, currentStart);
        }
        return LiveEdgeAppendDecision.Suppressed("no_ordered_live_extension");
    }

    private static bool Same(LiveEdgeBubble a, LiveEdgeBubble b) =>
        a.IsFullyVisible && b.IsFullyVisible && a.Side == b.Side &&
        a.CropFingerprint is not null && a.CropFingerprint == b.CropFingerprint &&
        a.Bounds.Width == b.Bounds.Width && a.Bounds.Height == b.Bounds.Height;
}
