using System.IO;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Observer;
using WeChatJevHud.Ocr;
using WeChatJevHud.TypeSafe;
using WeChatJevHud.Vision;
using WeChatJevHud.Windows;

namespace WeChatJevHud.Panel;

public sealed class LiveMessageEventArgs : EventArgs
{
    public LiveMessageEventArgs(string transcript, string targetMessage, int skippedUntrusted)
    {
        Transcript = transcript;
        TargetMessage = targetMessage;
        SkippedUntrusted = skippedUntrusted;
    }

    /// <summary>The recent conversation as "对方:" / "我:" lines, ending at the new message.</summary>
    public string Transcript { get; }

    public string TargetMessage { get; }

    /// <summary>How many recent messages were left out because OCR did not trust them.</summary>
    public int SkippedUntrusted { get; }
}

public sealed class LiveSkippedEventArgs : EventArgs
{
    public LiveSkippedEventArgs(string label, string reason)
    {
        Label = label;
        Reason = reason;
    }

    /// <summary>What to show in place of the message text.</summary>
    public string Label { get; }

    public string Reason { get; }
}

public sealed class LiveStatusEventArgs : EventArgs
{
    public LiveStatusEventArgs(string message) => Message = message;

    public string Message { get; }
}

/// <summary>
/// Runs the Phase 1-4 pipeline (window -> capture -> detect -> OCR -> observer) on a
/// background loop and raises one event per new remote message that OCR trusts.
///
/// Nothing is written to disk: frames stay in memory and no chat text is persisted,
/// per AGENTS.md.
/// </summary>
public sealed class LiveConversationWatcher : IDisposable
{
    private const int RecentContextMessages = 12;

    private readonly int _intervalMilliseconds;
    private readonly TesseractOcrEngine _tesseractRaw;
    private readonly TesseractOcrEngine _tesseractUpscaled;
    private readonly IMessageObserver _observer;
    private readonly Win32WeChatWindowTracker _tracker = new();
    private readonly Win32ScreenRegionCapture _capture = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private bool _wasUnavailable;

    public LiveConversationWatcher(string tessdataDirectory, int intervalMilliseconds = 250)
    {
        _intervalMilliseconds = intervalMilliseconds;

        _tesseractRaw = new TesseractOcrEngine(tessdataDirectory, lowConfidenceThreshold: 0.90);
        _tesseractUpscaled = new TesseractOcrEngine(
            tessdataDirectory,
            lowConfidenceThreshold: 0.90,
            preparation: OcrImagePreparation.Upscaled);

        var adaptive = new AdaptiveOcrEngine(
            [_tesseractRaw, _tesseractUpscaled],
            new WindowsMediaOcrEngine("zh-Hans-CN", OcrImagePreparation.Upscaled),
            confidenceThreshold: 0.90);

        _observer = new MessageObserver(
            new DarkThemeChatRegionLocator(),
            new DarkThemeBubbleDetector(),
            adaptive,
            new ChatRoiChangeDetector(),
            new VisualConversationIdentityProvider());

        _observer.NewMessageObserved += OnNewMessageObserved;
        _observer.ConversationChanged += OnConversationChanged;
    }

    public event EventHandler<LiveMessageEventArgs>? RemoteMessageArrived;

    /// <summary>
    /// A new remote message arrived that cannot be judged. Raised so the panel can say
    /// so where the user is looking, instead of leaving the previous judgment on screen
    /// next to a message it was never about.
    /// </summary>
    public event EventHandler<LiveSkippedEventArgs>? RemoteMessageSkipped;

    public event EventHandler<LiveStatusEventArgs>? StatusChanged;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// Finds the pinned Tesseract models by walking up from the binary. Returns null
    /// rather than a guessed path so the caller can say what is missing.
    /// </summary>
    public static string? FindTessdata()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".ocr-cache", "tessdata");
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, "chi_sim.traineddata")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public async Task StopAsync()
    {
        var cancellation = _cancellation;
        var loop = _loop;
        if (cancellation is null || loop is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: Stop cancels the loop.
        }
        finally
        {
            cancellation.Dispose();
            _cancellation = null;
            _loop = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Report("实时模式已启动，等待新消息。");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var window = _tracker.Locate();
                if (window is null || !window.IsVisible || window.IsMinimized)
                {
                    if (!_wasUnavailable)
                    {
                        Report("暂停：微信不可见或已最小化。");
                        _wasUnavailable = true;
                    }
                }
                else
                {
                    if (_wasUnavailable)
                    {
                        Report("微信恢复可见，继续观察。");
                        _wasUnavailable = false;
                    }

                    var frame = _capture.Capture(window);
                    await _observer.ObserveAsync(frame, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WindowCaptureUnavailableException exception)
            {
                // Transient: WeChat can be mid-restore or mid-move. Say so rather than
                // dropping to a silent retry, so a permanent failure is still visible.
                Report($"这一帧没抓到：{exception.Message}");
            }

            try
            {
                await Task.Delay(_intervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Report("实时模式已停止。");
    }

    private void OnConversationChanged(object? sender, ConversationChangedEventArgs e)
    {
        Report(e.PreviousEpoch is null
            ? "已建立当前会话基线。"
            : $"检测到会话切换（{e.PreviousEpoch.Id} → {e.CurrentEpoch.Id}），重新建立基线。");
    }

    private void OnNewMessageObserved(object? sender, MessageObservedEventArgs e)
    {
        var message = e.Message;
        if (message.Side != MessageSide.Remote)
        {
            return;
        }

        if (message.OcrStatus == OcrTextStatus.NoText)
        {
            // A sticker, image, emoji or voice note. A real turn with nothing to judge.
            RemoteMessageSkipped?.Invoke(
                this,
                new LiveSkippedEventArgs(
                    TranscriptBuilder.TextlessPlaceholder,
                    "这条没有文字，没有做判定。它已计入下一条的上下文。"));
            return;
        }

        if (!message.IsTrustedForSemantics)
        {
            RemoteMessageSkipped?.Invoke(
                this,
                new LiveSkippedEventArgs(
                    "[没认出来]",
                    $"新消息的 OCR 不可信（{message.OcrStatus}），没有做判定。"));
            return;
        }

        var built = BuildTranscript(message);
        if (built is null)
        {
            return;
        }

        RemoteMessageArrived?.Invoke(
            this,
            new LiveMessageEventArgs(built.Transcript, message.NormalizedText, built.SkippedUntrusted));
    }

    /// <summary>
    /// Maps the observer snapshot onto <see cref="TranscriptBuilder"/>. The snapshot can
    /// move on between the event and this read, so the target message is appended when it
    /// is no longer in the list rather than dropping the judgment.
    /// </summary>
    private TranscriptBuildResult? BuildTranscript(ObservedMessage target)
    {
        var messages = _observer.State.Messages;
        var turns = new List<TranscriptTurn>(messages.Count + 1);
        var containsTarget = false;

        foreach (var message in messages)
        {
            turns.Add(ToTurn(message));
            containsTarget |= string.Equals(message.Id, target.Id, StringComparison.Ordinal);
        }

        if (!containsTarget)
        {
            turns.Add(ToTurn(target));
        }

        return TranscriptBuilder.Build(turns, target.Id, RecentContextMessages);
    }

    private static TranscriptTurn ToTurn(ObservedMessage message) =>
        new(
            message.Id,
            message.Side switch
            {
                MessageSide.Remote => TranscriptSpeaker.Remote,
                MessageSide.Self => TranscriptSpeaker.Self,
                _ => TranscriptSpeaker.Other,
            },
            message.NormalizedText,
            message.IsTrustedForSemantics,
            message.OcrStatus == OcrTextStatus.NoText);

    private void Report(string message) =>
        StatusChanged?.Invoke(this, new LiveStatusEventArgs(message));

    public void Dispose()
    {
        _observer.NewMessageObserved -= OnNewMessageObserved;
        _observer.ConversationChanged -= OnConversationChanged;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _tesseractRaw.Dispose();
        _tesseractUpscaled.Dispose();
    }
}
