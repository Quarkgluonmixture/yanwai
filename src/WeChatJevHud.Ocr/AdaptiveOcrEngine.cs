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
        var evidence = new List<OcrEngineEvidence>(_scoredCandidates.Count + 1);
        foreach (var candidate in _scoredCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await candidate.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
            evidence.Add(ToEvidence(candidate.Name, result));
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
            return WithEvidence(best with { Status = OcrTextStatus.Recognized }, evidence);
        }

        var fallback = await _fallback.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
        evidence.Add(ToEvidence(_fallback.Name, fallback));
        var selected = fallback.Status == OcrTextStatus.Recognized
            ? fallback with { Status = OcrTextStatus.LowConfidence }
            : fallback;
        return WithEvidence(selected, evidence);
    }

    private static OcrResult WithEvidence(
        OcrResult result,
        IReadOnlyList<OcrEngineEvidence> evidence) =>
        result with
        {
            Diagnostics = new OcrDiagnostics(
                OcrRoute.Adaptive,
                result.IsTrustedForSemantics ? OcrTrustBasis.AdaptiveTrusted : OcrTrustBasis.None,
                evidence,
                TimeSpan.Zero),
        };

    private static OcrEngineEvidence ToEvidence(string name, OcrResult result) =>
        new(
            name,
            result.RawText,
            result.Status,
            result.OcrConfidence,
            null,
            null,
            null,
            null);
}
