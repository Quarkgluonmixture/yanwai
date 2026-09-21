namespace WeChatJevHud.Ocr;

public interface IPaddleRecognitionClient : IAsyncDisposable
{
    PaddleWorkerRuntimeInfo? RuntimeInfo { get; }

    ProductionOcrCounters Counters { get; }

    Task<PaddleWorkerRuntimeInfo> InitializeAsync(CancellationToken cancellationToken);

    Task<PaddleRecognition> RecognizeAsync(byte[] pngBytes, CancellationToken cancellationToken);
}

public sealed record PaddleWorkerOptions(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan StartupTimeout,
    TimeSpan RequestTimeout,
    TimeSpan RestartCooldown)
{
    public static PaddleWorkerOptions Create(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? startupTimeout = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? restartCooldown = null) =>
        new(
            fileName,
            arguments.ToArray(),
            startupTimeout ?? TimeSpan.FromSeconds(90),
            requestTimeout ?? TimeSpan.FromSeconds(8),
            restartCooldown ?? TimeSpan.FromSeconds(5));
}

public sealed record PaddleWorkerRuntimeInfo(
    string ModelName,
    string PaddleOcrVersion,
    string PaddlePaddleVersion,
    string RequestedDevice,
    string ActiveDevice,
    TimeSpan StartupElapsed,
    TimeSpan WarmupElapsed,
    string DetectorModel = "PP-OCRv6_small_det")
{
    public string RecognizerModel => ModelName;
}

public sealed record PaddleRecognition(
    string RequestId,
    string RawText,
    double? RecScore,
    TimeSpan InferenceElapsed,
    TimeSpan RoundtripElapsed,
    UnifiedExtraction? Extraction = null)
{
    public TimeSpan TransportElapsed =>
        RoundtripElapsed > (Extraction?.WorkerTotalElapsed ?? InferenceElapsed)
            ? RoundtripElapsed - (Extraction?.WorkerTotalElapsed ?? InferenceElapsed)
            : TimeSpan.Zero;
}

public sealed record PaddleLine(string RawText, double RecScore);

public sealed record UnifiedExtraction(
    int DetectedLineCount,
    IReadOnlyList<int[]> LineBoxes,
    IReadOnlyList<PaddleLine> Lines,
    TimeSpan DetectionElapsed,
    TimeSpan RecognitionElapsed,
    TimeSpan WorkerTotalElapsed);

public sealed class ProductionOcrCounters
{
    private long _workerStarts;
    private long _workerRestarts;
    private long _requests;
    private long _failures;
    private long _timeouts;
    private long _fallbacks;
    private long _inferenceTicks;
    private long _roundtripTicks;
    private long _workerTotalTicks;

    public ProductionOcrCounterSnapshot Snapshot => new(
        Interlocked.Read(ref _workerStarts),
        Interlocked.Read(ref _workerRestarts),
        Interlocked.Read(ref _requests),
        Interlocked.Read(ref _failures),
        Interlocked.Read(ref _timeouts),
        Interlocked.Read(ref _fallbacks),
        TimeSpan.FromTicks(Interlocked.Read(ref _inferenceTicks)),
        TimeSpan.FromTicks(Interlocked.Read(ref _roundtripTicks)),
        TimeSpan.FromTicks(Interlocked.Read(ref _workerTotalTicks)));

    internal void WorkerStarted(bool restart)
    {
        Interlocked.Increment(ref _workerStarts);
        if (restart)
        {
            Interlocked.Increment(ref _workerRestarts);
        }
    }

    internal void RequestStarted() => Interlocked.Increment(ref _requests);

    internal void RequestFailed() => Interlocked.Increment(ref _failures);

    internal void RequestTimedOut()
    {
        Interlocked.Increment(ref _failures);
        Interlocked.Increment(ref _timeouts);
    }

    public void FallbackUsed() => Interlocked.Increment(ref _fallbacks);

    internal void Timings(TimeSpan inference, TimeSpan roundtrip, TimeSpan workerTotal)
    {
        Interlocked.Add(ref _inferenceTicks, inference.Ticks);
        Interlocked.Add(ref _roundtripTicks, roundtrip.Ticks);
        Interlocked.Add(ref _workerTotalTicks, workerTotal.Ticks);
    }
}

public sealed record ProductionOcrCounterSnapshot(
    long PaddleWorkerStarts,
    long PaddleWorkerRestarts,
    long PaddleRequests,
    long PaddleFailures,
    long PaddleTimeouts,
    long PaddleFallbacks,
    TimeSpan PaddleInference,
    TimeSpan PaddleRoundtrip,
    TimeSpan PaddleWorkerTotal = default);
