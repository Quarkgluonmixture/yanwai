namespace WeChatJevHud.Ocr;

public sealed class AdaptiveOcrEngine : IOcrEngine
{
    private readonly IReadOnlyList<IOcrEngine> _scoredCandidates;
    private readonly IOcrEngine _fallback;
    private readonly double _confidenceThreshold;

    public AdaptiveOcrEngine(
        IReadOnlyList<IOcrEngine> scoredCandidates,
        IOcrEngine fallback,
        double confidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(scoredCandidates);
        ArgumentNullException.ThrowIfNull(fallback);
        if (scoredCandidates.Count == 0)
        {
            throw new ArgumentException("At least one confidence-bearing OCR candidate is required.", nameof(scoredCandidates));
        }

        if (confidenceThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidenceThreshold),
                "Confidence threshold must be between zero and one.");
        }

        _scoredCandidates = scoredCandidates;
        _fallback = fallback;
        _confidenceThreshold = confidenceThreshold;
    }

    public string Name => "adaptive-ocr";

    public async Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
    {
        OcrResult? best = null;
        foreach (var candidate in _scoredCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await candidate.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
            if (result.Status is OcrTextStatus.NoText or OcrTextStatus.Unsupported ||
                result.OcrConfidence is not { } confidence ||
                string.IsNullOrEmpty(result.Text))
            {
                continue;
            }

            if (best?.OcrConfidence is null || confidence > best.OcrConfidence.Value)
            {
                best = result;
            }
        }

        if (best?.OcrConfidence is { } bestConfidence && bestConfidence >= _confidenceThreshold)
        {
            return best with { Status = OcrTextStatus.Recognized };
        }

        var fallback = await _fallback.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
        return fallback.Status == OcrTextStatus.Recognized
            ? fallback with { Status = OcrTextStatus.LowConfidence }
            : fallback;
    }
}
