using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WeChatJevHud.Ocr;

public sealed class PaddleWorkerClient : IPaddleRecognitionClient
{
    private const int MaximumReportedStderrLines = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PaddleWorkerOptions _options;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task? _stderrPump;
    private bool _hasStarted;
    private bool _disposed;
    private DateTimeOffset _retryNotBefore;
    private int _stderrLinesObserved;

    public PaddleWorkerClient(
        PaddleWorkerOptions options,
        ProductionOcrCounters? counters = null,
        Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Counters = counters ?? new ProductionOcrCounters();
        _log = log;
    }

    public PaddleWorkerRuntimeInfo? RuntimeInfo { get; private set; }

    public ProductionOcrCounters Counters { get; }

    public async Task<PaddleWorkerRuntimeInfo> InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PaddleRecognition> RecognizeAsync(
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var requestId = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(
                new
                {
                    type = "recognize",
                    request_id = requestId,
                    image_base64 = Convert.ToBase64String(pngBytes),
                },
                JsonOptions);
            var timer = Stopwatch.StartNew();
            Counters.RequestStarted();
            await _process!.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            string? line;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(_options.RequestTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Counters.RequestTimedOut();
                StopWorker(scheduleRetry: true);
                throw new PaddleWorkerException("Paddle worker request timed out.");
            }

            timer.Stop();
            if (line is null)
            {
                Counters.RequestFailed();
                StopWorker(scheduleRetry: true);
                throw new PaddleWorkerException("Paddle worker exited before returning a response.");
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = RequiredString(root, "type");
                var responseId = RequiredString(root, "request_id");
                if (!string.Equals(responseId, requestId, StringComparison.Ordinal))
                {
                    throw new PaddleWorkerException(
                        $"Paddle worker response ID '{responseId}' did not match request '{requestId}'.");
                }

                if (type == "error")
                {
                    throw new PaddleWorkerException(
                        root.TryGetProperty("message", out var message)
                            ? message.GetString() ?? "Paddle worker request failed."
                            : "Paddle worker request failed.");
                }

                if (type != "result")
                {
                    throw new PaddleWorkerException($"Unexpected Paddle worker response type '{type}'.");
                }

                var inference = ReadElapsed(root, "inference_ms");
                var count = root.GetProperty("detected_line_count").GetInt32();
                var boxes = root.GetProperty("line_boxes").EnumerateArray()
                    .Select(box => box.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
                var lines = root.GetProperty("lines").EnumerateArray().Select(item =>
                    new PaddleLine(RequiredString(item, "raw_text"), item.GetProperty("rec_score").GetDouble())).ToArray();
                if (count < 0 || boxes.Length != count || lines.Length != Math.Max(1, count) ||
                    boxes.Any(box => box.Length != 4 || box[0] < 0 || box[1] < 0 || box[2] <= box[0] || box[3] <= box[1]) ||
                    lines.Any(item => !double.IsFinite(item.RecScore)))
                {
                    throw new PaddleWorkerException("Invalid Unified extraction structure.");
                }
                var extraction = new UnifiedExtraction(count, boxes, lines,
                    ReadElapsed(root, "detection_ms"), ReadElapsed(root, "recognition_ms"), ReadElapsed(root, "worker_total_ms"));
                var recognition = new PaddleRecognition(
                    responseId,
                    RequiredString(root, "raw_text"),
                    root.GetProperty("rec_score").ValueKind == JsonValueKind.Null ? null : root.GetProperty("rec_score").GetDouble(),
                    inference,
                    timer.Elapsed,
                    extraction);
                Counters.Timings(recognition.InferenceElapsed, recognition.RoundtripElapsed, extraction.WorkerTotalElapsed);
                return recognition;
            }
            catch (PaddleWorkerException)
            {
                Counters.RequestFailed();
                StopWorker(scheduleRetry: true);
                throw;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                Counters.RequestFailed();
                StopWorker(scheduleRetry: true);
                throw new PaddleWorkerException("Paddle worker returned a malformed response.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_process is { HasExited: false } process)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("{\"type\":\"shutdown\"}").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
                {
                    _log?.Invoke($"Paddle worker clean shutdown failed: {exception.Message}");
                }
            }

            StopWorker();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }

        if (_stderrPump is not null)
        {
            await _stderrPump.ConfigureAwait(false);
        }
    }

    private async Task<PaddleWorkerRuntimeInfo> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && RuntimeInfo is not null)
        {
            return RuntimeInfo;
        }

        if (DateTimeOffset.UtcNow < _retryNotBefore)
        {
            throw new PaddleWorkerException(
                $"Paddle worker restart is cooling down until {_retryNotBefore:O}.");
        }

        StopWorker();
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in _options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var restart = _hasStarted;
        try
        {
            _process = Process.Start(startInfo)
                ?? throw new PaddleWorkerException("Failed to start Paddle worker process.");
        }
        catch (Exception exception) when (exception is not PaddleWorkerException)
        {
            Counters.RequestFailed();
            _retryNotBefore = DateTimeOffset.UtcNow + _options.RestartCooldown;
            throw new PaddleWorkerException("Failed to start Paddle worker process.", exception);
        }
        _hasStarted = true;
        Counters.WorkerStarted(restart);
        _stderrPump = PumpStderrAsync(_process);
        string? line;
        try
        {
            line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(_options.StartupTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            Counters.RequestTimedOut();
            StopWorker(scheduleRetry: true);
            throw new PaddleWorkerException("Paddle worker READY handshake timed out.", exception);
        }

        try
        {
            if (line is null)
            {
                throw new PaddleWorkerException("Paddle worker exited before READY.");
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (RequiredString(root, "type") != "ready")
            {
                throw new PaddleWorkerException("Paddle worker did not emit a READY handshake.");
            }

            if (root.GetProperty("protocol_version").GetInt32() != 2 ||
                RequiredString(root, "detector_model") != "PP-OCRv6_small_det" ||
                RequiredString(root, "recognizer_model") != "PP-OCRv6_small_rec")
            {
                throw new PaddleWorkerException("Unsupported Unified worker handshake.");
            }
            RuntimeInfo = new PaddleWorkerRuntimeInfo(
                RequiredString(root, "recognizer_model"),
                RequiredString(root, "paddleocr_version"),
                RequiredString(root, "paddlepaddle_version"),
                RequiredString(root, "device_requested"),
                RequiredString(root, "device_active"),
                TimeSpan.FromMilliseconds(root.GetProperty("startup_ms").GetDouble()),
                TimeSpan.FromMilliseconds(root.GetProperty("warmup_ms").GetDouble()),
                RequiredString(root, "detector_model"));
            if (RuntimeInfo.RequestedDevice.StartsWith("gpu", StringComparison.OrdinalIgnoreCase) &&
                !RuntimeInfo.ActiveDevice.StartsWith("gpu", StringComparison.OrdinalIgnoreCase))
            {
                throw new PaddleWorkerException(
                    $"Paddle requested {RuntimeInfo.RequestedDevice} but activated {RuntimeInfo.ActiveDevice}.");
            }

            _retryNotBefore = DateTimeOffset.MinValue;
            return RuntimeInfo;
        }
        catch (PaddleWorkerException)
        {
            Counters.RequestFailed();
            StopWorker(scheduleRetry: true);
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Counters.RequestFailed();
            StopWorker(scheduleRetry: true);
            throw new PaddleWorkerException("Paddle worker returned a malformed READY handshake.", exception);
        }
    }

    private static TimeSpan ReadElapsed(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetDouble();
        if (!double.IsFinite(value) || value < 0 || value > TimeSpan.MaxValue.TotalMilliseconds)
        {
            throw new PaddleWorkerException("Invalid worker timing.");
        }
        return TimeSpan.FromMilliseconds(value);
    }

    private async Task PumpStderrAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (_log is null)
            {
                continue;
            }

            var lineNumber = Interlocked.Increment(ref _stderrLinesObserved);
            if (lineNumber <= MaximumReportedStderrLines)
            {
                _log($"worker stderr received category={ClassifyDiagnostic(line)} chars={line.Length}");
            }
            else if (lineNumber == MaximumReportedStderrLines + 1)
            {
                _log($"worker stderr output suppressed after {MaximumReportedStderrLines} lines");
            }
        }
    }

    private static string ClassifyDiagnostic(string line)
    {
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        return "info";
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new PaddleWorkerException($"Missing string property '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private void StopWorker(bool scheduleRetry = false)
    {
        if (scheduleRetry)
        {
            _retryNotBefore = DateTimeOffset.UtcNow + _options.RestartCooldown;
        }

        RuntimeInfo = null;
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        _process = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed class PaddleWorkerException : Exception
{
    public PaddleWorkerException(string message)
        : base(message)
    {
    }

    public PaddleWorkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
