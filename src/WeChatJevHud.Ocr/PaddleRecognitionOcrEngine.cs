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
                OcrRoute.PaddleSingleLine,
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
                recognition.RoundtripElapsed));
    }
}
