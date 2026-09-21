using System.Diagnostics;

namespace WeChatJevHud.Ocr;

public sealed class ProductionOcrCalibrationEvaluator
{
    public async Task<IReadOnlyList<ProductionOcrCalibrationRow>> EvaluateAsync(
        IOcrEngine engine,
        IReadOnlyList<OcrEvaluationFixture> fixtures,
        CancellationToken cancellationToken)
    {
        var rows = new List<ProductionOcrCalibrationRow>(fixtures.Count);
        foreach (var fixture in fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            var result = await engine.RecognizeAsync(fixture.Crop, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            var diagnostics = result.Diagnostics
                ?? throw new InvalidOperationException("Production OCR result has no diagnostics.");
            var paddle = diagnostics.Evidence.FirstOrDefault(
                item => item.EngineScoreKind == "paddle_rec_score");
            var secondary = diagnostics.Evidence.FirstOrDefault(
                item => item.EngineName == "adaptive-ocr");
            var normalizedExpected = OcrTextNormalizer.Normalize(fixture.Expected);
            var normalizedFinal = OcrTextNormalizer.Normalize(result.RawText);
            rows.Add(new ProductionOcrCalibrationRow(
                fixture.Name,
                fixture.Expected,
                diagnostics.Route,
                paddle?.RawText,
                paddle?.EngineScore,
                secondary?.RawText,
                secondary?.Status,
                result.RawText,
                result.Text,
                result.Status,
                result.IsTrustedForSemantics,
                diagnostics.TrustBasis,
                string.Equals(fixture.Expected, result.RawText, StringComparison.Ordinal),
                string.Equals(normalizedExpected, normalizedFinal, StringComparison.Ordinal),
                CharacterErrorRate(fixture.Expected, result.RawText),
                CharacterErrorRate(normalizedExpected, normalizedFinal),
                paddle?.InferenceElapsed,
                paddle?.RoundtripElapsed,
                timer.Elapsed,
                diagnostics));
        }

        return rows;
    }

    private static double CharacterErrorRate(string expected, string recognized)
    {
        if (expected.Length == 0)
        {
            return recognized.Length == 0 ? 0 : 1;
        }

        var previous = Enumerable.Range(0, recognized.Length + 1).ToArray();
        var current = new int[recognized.Length + 1];
        for (var row = 1; row <= expected.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= recognized.Length; column++)
            {
                var substitution = expected[row - 1] == recognized[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[recognized.Length] / (double)expected.Length;
    }
}

public sealed record ProductionOcrCalibrationRow(
    string Fixture,
    string Expected,
    OcrRoute Route,
    string? PaddleRaw,
    double? PaddleRecScore,
    string? SecondaryRaw,
    OcrTextStatus? SecondaryStatus,
    string FinalRawText,
    string FinalText,
    OcrTextStatus FinalStatus,
    bool IsTrustedForSemantics,
    OcrTrustBasis TrustBasis,
    bool RawExactMatch,
    bool NormalizedMatch,
    double RawCharacterErrorRate,
    double NormalizedCharacterErrorRate,
    TimeSpan? PaddleInference,
    TimeSpan? PaddleRoundtrip,
    TimeSpan TotalOcr,
    OcrDiagnostics? Diagnostics = null);
