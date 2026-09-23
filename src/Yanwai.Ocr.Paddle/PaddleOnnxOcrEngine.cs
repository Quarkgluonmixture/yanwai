using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Yanwai.Capture;
using Yanwai.Core.Geometry;

namespace Yanwai.Ocr;

/// <summary>
/// PP-OCRv6 small text recognition, run locally through ONNX Runtime.
///
/// Recognition only: bubble geometry comes from the Phase 2 detector and lines from
/// <see cref="TextLineSplitter"/>; Paddle's own text detector is never used.
///
/// Every result is <see cref="OcrTextStatus.LowConfidence"/> on its own. The model's
/// score is not calibrated — it has been measured at 1.00 on a line missing a
/// character — so trust has to come from <see cref="CrossCheckedOcrEngine"/>.
/// </summary>
public sealed class PaddleOnnxOcrEngine : IOcrEngine, IDisposable
{
    private const int LineHeight = 48;
    private const int MinimumLineWidth = 8;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _characters;
    private readonly HashSet<char> _emittable;

    public PaddleOnnxOcrEngine(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        var model = Path.Combine(modelDirectory, "inference.onnx");
        var config = Path.Combine(modelDirectory, "inference.yml");
        if (!File.Exists(model) || !File.Exists(config))
        {
            throw new FileNotFoundException(
                $"PP-OCR model files are missing in {modelDirectory}. Run scripts\\install-ocr-models.ps1.");
        }

        // Index 0 is the CTC blank; the dictionary follows, then the space PP-OCR appends.
        var dictionary = PaddleCharacterDictionary.Read(config);
        _characters = ["", .. dictionary, " "];
        _emittable = _characters.Where(entry => entry.Length == 1).Select(entry => entry[0]).ToHashSet();

        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(model, options);
        _inputName = _session.InputMetadata.Keys.Single();

        var classes = _session.OutputMetadata.Values.Single().Dimensions[^1];
        if (classes != _characters.Length)
        {
            // A dictionary that does not line up with the output would shift every
            // character by one and still produce plausible-looking text.
            throw new InvalidDataException(
                $"PP-OCR dictionary has {_characters.Length} classes but the model outputs {classes}.");
        }
    }

    public string Name => "paddle-ppocrv6-small-rec";

    /// <summary>False for a character this model can never output, such as 诶.</summary>
    public bool CanEmit(char character) => _emittable.Contains(character);

    public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(crop);
        cancellationToken.ThrowIfCancellationRequested();

        var bubble = ImageCropExtractor.Extract(crop);
        var lines = TextLineSplitter.Split(bubble);
        var text = new StringBuilder();
        var raw = new StringBuilder();
        var probabilities = new List<float>();

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (lineText, lineProbabilities) = RecognizeLine(bubble, line);
            if (lineText.Length == 0)
            {
                continue;
            }

            if (raw.Length > 0)
            {
                raw.Append('\n');
            }

            raw.Append(lineText);
            text.Append(lineText);
            probabilities.AddRange(lineProbabilities);
        }

        var normalized = OcrTextNormalizer.Normalize(text.ToString());
        return Task.FromResult(normalized.Length == 0
            ? new OcrResult(string.Empty, null, OcrTextStatus.NoText, raw.ToString())
            : new OcrResult(normalized, probabilities.Average(), OcrTextStatus.LowConfidence, raw.ToString()));
    }

    private (string Text, IReadOnlyList<float> Probabilities) RecognizeLine(CapturedFrame bubble, CapturePixelRect line)
    {
        var width = Math.Max(MinimumLineWidth, (int)Math.Ceiling(LineHeight * (double)line.Width / line.Height));
        var tensor = new DenseTensor<float>([1, 3, LineHeight, width]);

        // Bilinear resize straight into the normalized BGR planes: (v / 255 - 0.5) / 0.5.
        var scaleX = (double)line.Width / width;
        var scaleY = (double)line.Height / LineHeight;
        for (var y = 0; y < LineHeight; y++)
        {
            var sourceY = Math.Clamp(((y + 0.5) * scaleY) - 0.5, 0, line.Height - 1);
            var y0 = (int)sourceY;
            var y1 = Math.Min(y0 + 1, line.Height - 1);
            var fy = sourceY - y0;
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp(((x + 0.5) * scaleX) - 0.5, 0, line.Width - 1);
                var x0 = (int)sourceX;
                var x1 = Math.Min(x0 + 1, line.Width - 1);
                var fx = sourceX - x0;
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = Lerp(Pixel(x0, y0, channel), Pixel(x1, y0, channel), fx);
                    var bottom = Lerp(Pixel(x0, y1, channel), Pixel(x1, y1, channel), fx);
                    var value = Lerp(top, bottom, fy);
                    tensor[0, channel, y, x] = (float)(((value / 255.0) - 0.5) / 0.5);
                }
            }
        }

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var output = results[0].AsTensor<float>();
        var steps = output.Dimensions[1];
        var classes = output.Dimensions[2];

        // Greedy CTC: best class per step, drop blanks and repeats.
        var text = new StringBuilder();
        var probabilities = new List<float>();
        var previous = -1;
        for (var step = 0; step < steps; step++)
        {
            var best = 0;
            var bestProbability = output[0, step, 0];
            for (var index = 1; index < classes; index++)
            {
                var probability = output[0, step, index];
                if (probability > bestProbability)
                {
                    best = index;
                    bestProbability = probability;
                }
            }

            if (best != 0 && best != previous)
            {
                text.Append(_characters[best]);
                probabilities.Add(bestProbability);
            }

            previous = best;
        }

        return (text.ToString(), probabilities);

        // BGRA in memory; BGR is exactly the channel order the model was trained on.
        double Pixel(int x, int y, int channel) =>
            bubble.Bgra32Pixels[((line.Y + y) * bubble.Stride) + ((line.X + x) * 4) + channel];
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    public void Dispose() => _session.Dispose();
}
