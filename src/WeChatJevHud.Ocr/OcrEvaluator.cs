using System.Diagnostics;

namespace WeChatJevHud.Ocr;

public sealed class OcrEvaluator
{
    public async Task<IReadOnlyList<OcrEvaluationRow>> EvaluateAsync(
        IOcrEngine engine,
        IReadOnlyList<OcrEvaluationFixture> fixtures,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(fixtures);

        var rows = new List<OcrEvaluationRow>(fixtures.Count);
        foreach (var fixture in fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            var result = await engine.RecognizeAsync(fixture.Crop, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            var rawRecognized = result.RawText;
            var normalizedExpected = OcrTextNormalizer.Normalize(fixture.Expected);
            var normalizedRecognized = OcrTextNormalizer.Normalize(rawRecognized);
            rows.Add(new OcrEvaluationRow(
                engine.Name,
                fixture.Name,
                fixture.Expected,
                rawRecognized,
                normalizedRecognized,
                result.OcrConfidence,
                timer.Elapsed,
                result.Status,
                string.Equals(fixture.Expected, rawRecognized, StringComparison.Ordinal),
                string.Equals(normalizedExpected, normalizedRecognized, StringComparison.Ordinal),
                CharacterErrorRate(fixture.Expected, rawRecognized),
                CharacterErrorRate(normalizedExpected, normalizedRecognized)));
        }

        return rows;
    }

    private static double CharacterErrorRate(string expected, string recognized)
    {
        if (expected.Length == 0)
        {
            return recognized.Length == 0 ? 0 : 1;
        }

        var previous = new int[recognized.Length + 1];
        var current = new int[recognized.Length + 1];
        for (var column = 0; column <= recognized.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= expected.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= recognized.Length; column++)
            {
                var substitutionCost = expected[row - 1] == recognized[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[recognized.Length] / (double)expected.Length;
    }
}

public sealed record OcrEvaluationFixture(string Name, string Expected, ImageCrop Crop);

public sealed record OcrEvaluationRow(
    string Engine,
    string Fixture,
    string Expected,
    string RawRecognized,
    string NormalizedRecognized,
    double? OcrConfidence,
    TimeSpan Elapsed,
    OcrTextStatus Status,
    bool RawExactMatch,
    bool NormalizedMatch,
    double RawCharacterErrorRate,
    double NormalizedCharacterErrorRate)
{
    public bool ExactMatch => RawExactMatch;
}
