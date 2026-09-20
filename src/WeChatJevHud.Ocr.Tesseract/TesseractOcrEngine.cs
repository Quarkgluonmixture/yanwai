using Tesseract;

namespace WeChatJevHud.Ocr;

public sealed class TesseractOcrEngine : IOcrEngine, IDisposable
{
    private readonly TesseractEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly double _lowConfidenceThreshold;
    private readonly OcrImagePreparation _preparation;
    private bool _disposed;

    public TesseractOcrEngine(
        string dataPath,
        string languages = "chi_sim+eng",
        double lowConfidenceThreshold = 0.60,
        OcrImagePreparation preparation = OcrImagePreparation.Raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(languages);
        if (lowConfidenceThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lowConfidenceThreshold),
                "Confidence threshold must be between zero and one.");
        }

        Languages = languages;
        _lowConfidenceThreshold = lowConfidenceThreshold;
        _preparation = preparation;
        _engine = new TesseractEngine(dataPath, languages, EngineMode.LstmOnly);
    }

    public string Languages { get; }

    public string Name => $"tesseract:{Languages}:{PreparationName(_preparation)}";

    public async Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var png = ImageCropPngEncoder.Encode(crop, _preparation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var image = Pix.LoadFromMemory(png);
            using var page = _engine.Process(image, PageSegMode.SingleBlock);
            var rawText = page.GetText();
            var text = OcrTextNormalizer.Normalize(rawText);
            if (string.IsNullOrEmpty(text))
            {
                return new OcrResult(
                    string.Empty,
                    page.GetMeanConfidence(),
                    OcrTextStatus.NoText,
                    rawText);
            }

            var confidence = Math.Clamp(page.GetMeanConfidence(), 0, 1);
            return new OcrResult(
                text,
                confidence,
                confidence < _lowConfidenceThreshold
                    ? OcrTextStatus.LowConfidence
                    : OcrTextStatus.Recognized,
                rawText);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _engine.Dispose();
        _gate.Dispose();
        _disposed = true;
    }

    private static string PreparationName(OcrImagePreparation preparation) => preparation switch
    {
        OcrImagePreparation.Raw => "raw",
        OcrImagePreparation.Upscaled => "upscaled",
        _ => throw new ArgumentOutOfRangeException(nameof(preparation)),
    };
}
