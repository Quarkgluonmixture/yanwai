using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WeChatJevHud.Ocr;

public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    private readonly OcrEngine _engine;
    private readonly OcrImagePreparation _preparation;

    public WindowsMediaOcrEngine(
        string languageTag,
        OcrImagePreparation preparation = OcrImagePreparation.Raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageTag);
        var language = new Language(languageTag);
        _engine = OcrEngine.TryCreateFromLanguage(language)
            ?? throw new NotSupportedException($"Windows OCR language '{languageTag}' is not installed.");
        _preparation = preparation;
    }

    public string Name => $"windows-media-ocr:{_engine.RecognizerLanguage.LanguageTag}:{PreparationName(_preparation)}";

    public async Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var png = ImageCropPngEncoder.Encode(crop, _preparation);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        if (decoder.PixelWidth > OcrEngine.MaxImageDimension || decoder.PixelHeight > OcrEngine.MaxImageDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(crop), "OCR crop exceeds the Windows OCR image dimension limit.");
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore);
        cancellationToken.ThrowIfCancellationRequested();
        var recognized = await _engine.RecognizeAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        var text = OcrTextNormalizer.Normalize(recognized.Text);
        return new OcrResult(
            text,
            OcrConfidence: null,
            Status: string.IsNullOrEmpty(text) ? OcrTextStatus.NoText : OcrTextStatus.Recognized);
    }

    private static string PreparationName(OcrImagePreparation preparation) => preparation switch
    {
        OcrImagePreparation.Raw => "raw",
        OcrImagePreparation.Upscaled => "upscaled",
        _ => throw new ArgumentOutOfRangeException(nameof(preparation)),
    };
}
