using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Ocr;

public interface IOcrEngine
{
    string Name { get; }

    Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken);
}

public sealed record ImageCrop(
    CapturedFrame Frame,
    CapturePixelRect Bounds,
    OcrCropRole Role = OcrCropRole.MainMessage);

public enum OcrCropRole
{
    MainMessage,
    QuotedText,
}

public sealed record OcrResult(
    string Text,
    double? OcrConfidence,
    OcrTextStatus Status,
    string RawText,
    OcrDiagnostics? Diagnostics = null)
{
    public bool IsTrustedForSemantics =>
        Status == OcrTextStatus.Recognized && !string.IsNullOrWhiteSpace(Text);
}

public sealed record OcrDiagnostics(
    OcrRoute Route,
    OcrTrustBasis TrustBasis,
    IReadOnlyList<OcrEngineEvidence> Evidence,
    TimeSpan TotalElapsed,
    UnifiedExtraction? Extraction = null,
    OcrCropRole RegionRole = OcrCropRole.MainMessage,
    bool QuoteSeparationUnverified = false,
    bool RuntimeFallback = false);

public sealed record OcrEngineEvidence(
    string EngineName,
    string RawText,
    OcrTextStatus Status,
    double? OcrConfidence,
    double? EngineScore,
    string? EngineScoreKind,
    TimeSpan? InferenceElapsed,
    TimeSpan? RoundtripElapsed);

public enum OcrRoute
{
    Adaptive,
    PaddleSingleLine,
    PaddleUnified,
}

public enum OcrTrustBasis
{
    None,
    AdaptiveTrusted,
    IndependentEngineAgreement,
}

public enum OcrTextStatus
{
    Recognized,
    LowConfidence,
    NoText,
    Unsupported,
}
