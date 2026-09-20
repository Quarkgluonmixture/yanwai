using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Ocr;

public interface IOcrEngine
{
    string Name { get; }

    Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken);
}

public sealed record ImageCrop(CapturedFrame Frame, CapturePixelRect Bounds);

public sealed record OcrResult(string Text, double? OcrConfidence, OcrTextStatus Status);

public enum OcrTextStatus
{
    Recognized,
    LowConfidence,
    NoText,
    Unsupported,
}
