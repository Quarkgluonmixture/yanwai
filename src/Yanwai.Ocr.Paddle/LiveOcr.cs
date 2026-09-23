using System.IO;

namespace Yanwai.Ocr;

/// <summary>
/// The OCR the live pipeline uses, defined once so the panel and the diagnostics
/// observe the same thing: PP-OCRv6 small reads, Windows OCR (line by line, dark on light) checks
/// (<see cref="CrossCheckedOcrEngine"/>).
/// </summary>
public sealed class LiveOcr : IDisposable
{
    public const string ModelFolder = "PP-OCRv6_small_rec";

    private readonly PaddleOnnxOcrEngine _paddle;

    private LiveOcr(PaddleOnnxOcrEngine paddle, IOcrEngine engine)
    {
        _paddle = paddle;
        Engine = engine;
    }

    public IOcrEngine Engine { get; }

    public static LiveOcr Create(string modelDirectory)
    {
        var paddle = new PaddleOnnxOcrEngine(modelDirectory);
        try
        {
            var checker = new LineWiseOcrEngine(new WindowsMediaOcrEngine("zh-Hans-CN", OcrImagePreparation.Upscaled));
            return new LiveOcr(paddle, new CrossCheckedOcrEngine(paddle, checker, paddle.CanEmit));
        }
        catch
        {
            paddle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Finds the pinned model by walking up from <paramref name="start"/>. Returns null
    /// rather than a guessed path so the caller can say what is missing.
    /// </summary>
    public static string? FindModelDirectory(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".ocr-cache", "paddle", ModelFolder);
            if (File.Exists(Path.Combine(candidate, "inference.onnx")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public void Dispose() => _paddle.Dispose();
}
