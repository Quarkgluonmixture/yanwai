using System.Diagnostics;

namespace WeChatJevHud.Ocr;

public sealed class RoutedOcrEngine : IOcrEngine
{
    private readonly IOcrRoutingPolicy _routingPolicy;
    private readonly IOcrEngine _paddle;
    private readonly IOcrEngine _adaptive;
    private readonly ProductionOcrCounters? _counters;

    public RoutedOcrEngine(
        IOcrRoutingPolicy routingPolicy,
        IOcrEngine paddle,
        IOcrEngine adaptive,
        ProductionOcrCounters? counters = null)
    {
        _routingPolicy = routingPolicy ?? throw new ArgumentNullException(nameof(routingPolicy));
        _paddle = paddle ?? throw new ArgumentNullException(nameof(paddle));
        _adaptive = adaptive ?? throw new ArgumentNullException(nameof(adaptive));
        _counters = counters;
    }

    public string Name => "routed-production-ocr";

    public async Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var route = _routingPolicy.SelectRoute(crop);
        if (route == OcrRoute.Adaptive)
        {
            var adaptiveOnly = await _adaptive.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            return WithDiagnostics(
                adaptiveOnly,
                route,
                adaptiveOnly.IsTrustedForSemantics ? OcrTrustBasis.AdaptiveTrusted : OcrTrustBasis.None,
                [SelectedEvidence(_adaptive.Name, adaptiveOnly), .. AdaptiveEvidence(adaptiveOnly)],
                timer.Elapsed);
        }

        OcrResult paddle;
        try
        {
            paddle = await _paddle.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _counters?.FallbackUsed();
            var fallback = await _adaptive.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            return WithDiagnostics(
                fallback,
                route,
                fallback.IsTrustedForSemantics ? OcrTrustBasis.AdaptiveTrusted : OcrTrustBasis.None,
                [SelectedEvidence(_adaptive.Name, fallback), .. AdaptiveEvidence(fallback)],
                timer.Elapsed);
        }

        var adaptive = await _adaptive.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
        OcrEngineEvidence[] evidence =
        [
            SelectedEvidence(_paddle.Name, paddle),
            SelectedEvidence(_adaptive.Name, adaptive),
            .. AdaptiveEvidence(adaptive),
        ];
        var paddleText = OcrTextNormalizer.Normalize(paddle.RawText);
        var adaptiveText = OcrTextNormalizer.Normalize(adaptive.RawText);
        timer.Stop();

        var confidenceBearingAgreement = adaptive.Diagnostics?.Evidence.Any(item =>
            item.OcrConfidence is not null &&
            !string.IsNullOrWhiteSpace(item.RawText) &&
            string.Equals(
                paddleText,
                OcrTextNormalizer.Normalize(item.RawText),
                StringComparison.Ordinal)) == true;
        if (!string.IsNullOrWhiteSpace(paddleText) &&
            string.Equals(paddleText, adaptiveText, StringComparison.Ordinal) &&
            (adaptive.IsTrustedForSemantics || confidenceBearingAgreement))
        {
            return new OcrResult(
                paddleText,
                adaptive.OcrConfidence,
                OcrTextStatus.Recognized,
                paddle.RawText,
                new OcrDiagnostics(
                    route,
                    OcrTrustBasis.IndependentEngineAgreement,
                    evidence,
                    timer.Elapsed));
        }

        if (adaptive.IsTrustedForSemantics)
        {
            return WithDiagnostics(
                adaptive,
                route,
                OcrTrustBasis.AdaptiveTrusted,
                evidence,
                timer.Elapsed);
        }

        if (!string.IsNullOrWhiteSpace(paddleText))
        {
            return new OcrResult(
                paddleText,
                null,
                OcrTextStatus.LowConfidence,
                paddle.RawText,
                new OcrDiagnostics(route, OcrTrustBasis.None, evidence, timer.Elapsed));
        }

        return WithDiagnostics(adaptive, route, OcrTrustBasis.None, evidence, timer.Elapsed);
    }

    private static OcrResult WithDiagnostics(
        OcrResult result,
        OcrRoute route,
        OcrTrustBasis trustBasis,
        IReadOnlyList<OcrEngineEvidence> evidence,
        TimeSpan elapsed) =>
        result with { Diagnostics = new OcrDiagnostics(route, trustBasis, evidence, elapsed) };

    private static OcrEngineEvidence SelectedEvidence(string name, OcrResult result)
    {
        var engineSpecific = result.Diagnostics?.Evidence.FirstOrDefault(item =>
            item.EngineScoreKind is not null);
        return engineSpecific ?? new OcrEngineEvidence(
            name,
            result.RawText,
            result.Status,
            result.OcrConfidence,
            null,
            null,
            null,
            null);
    }

    private static IReadOnlyList<OcrEngineEvidence> AdaptiveEvidence(OcrResult result) =>
        result.Diagnostics?.Evidence ?? [];
}
