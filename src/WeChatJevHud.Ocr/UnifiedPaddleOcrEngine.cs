using System.Diagnostics;

namespace WeChatJevHud.Ocr;

/// <summary>Unified extraction with runtime-failure-only, untrusted degradation.</summary>
public sealed class UnifiedPaddleOcrEngine(
    IOcrEngine paddle,
    IOcrEngine adaptive,
    ProductionOcrCounters? counters = null) : IOcrEngine
{
    public string Name => "unified-paddle-ocr";

    public async Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            // No second opinion, confidence threshold, or NoText rerouting.
            var result = await paddle.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
            return result with
            {
                Diagnostics = result.Diagnostics is { } diagnostics
                    ? diagnostics with { TotalElapsed = timer.Elapsed }
                    : null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            counters?.FallbackUsed();
            var result = await adaptive.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
            return result with
            {
                Status = result.Status == OcrTextStatus.Recognized ? OcrTextStatus.LowConfidence : result.Status,
                Diagnostics = new OcrDiagnostics(
                    OcrRoute.Adaptive,
                    OcrTrustBasis.None,
                    result.Diagnostics?.Evidence ?? [new OcrEngineEvidence(
                        adaptive.Name, result.RawText, result.Status, result.OcrConfidence, null, null, null, null)],
                    timer.Elapsed,
                    RegionRole: crop.Role,
                    QuoteSeparationUnverified: crop.Role == OcrCropRole.MainMessage,
                    RuntimeFallback: true),
            };
        }
    }
}
