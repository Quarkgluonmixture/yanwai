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
            rows.Add(new OcrEvaluationRow(
                engine.Name,
                fixture.Name,
                fixture.Expected,
                result.Text,
                result.OcrConfidence,
                timer.Elapsed,
                result.Status));
        }

        return rows;
    }
}

public sealed record OcrEvaluationFixture(string Name, string Expected, ImageCrop Crop);

public sealed record OcrEvaluationRow(
    string Engine,
    string Fixture,
    string Expected,
    string Recognized,
    double? OcrConfidence,
    TimeSpan Elapsed,
    OcrTextStatus Status);
