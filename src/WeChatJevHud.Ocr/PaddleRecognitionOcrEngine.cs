namespace WeChatJevHud.Ocr;

public sealed class PaddleRecognitionOcrEngine : IOcrEngine
{
    private readonly IPaddleRecognitionClient _client;

    public PaddleRecognitionOcrEngine(IPaddleRecognitionClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => _client.RuntimeInfo?.ModelName ?? "PP-OCRv6_small_rec";

    public async Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
    {
        var png = ImageCropPngEncoder.Encode(crop);
        var recognition = await _client.RecognizeAsync(png, cancellationToken).ConfigureAwait(false);
        if (recognition.Extraction?.LineBoxes.Any(box =>
            box.Length != 4 || box[0] < 0 || box[1] < 0 || box[2] > crop.Bounds.Width ||
            box[3] > crop.Bounds.Height || box[2] <= box[0] || box[3] <= box[1]) == true)
        {
            throw new PaddleWorkerException("Worker line box exceeds the submitted crop.");
        }
        var normalized = OcrTextNormalizer.Normalize(recognition.RawText);
        var status = string.IsNullOrWhiteSpace(normalized)
            ? OcrTextStatus.NoText
            : OcrTextStatus.LowConfidence;
        return new OcrResult(
            normalized,
            null,
            status,
            recognition.RawText,
            new OcrDiagnostics(
                OcrRoute.PaddleUnified,
                OcrTrustBasis.None,
                [new OcrEngineEvidence(
                    Name,
                    recognition.RawText,
                    status,
                    null,
                    recognition.RecScore,
                    "paddle_rec_score",
                    recognition.InferenceElapsed,
                    recognition.RoundtripElapsed)],
                recognition.RoundtripElapsed,
                recognition.Extraction,
                crop.Role,
                QuoteSeparationUnverified: crop.Role == OcrCropRole.MainMessage));
    }
}
