using System.IO;
using System.Linq;
using Yanwai.Capture;
using Yanwai.Core.Geometry;
using Yanwai.Core.Messages;
using Yanwai.Core.Windows;
using Yanwai.Observer;
using Yanwai.Ocr;
using Yanwai.TypeSafe;
using Yanwai.Vision;
using Yanwai.Windows;

namespace Yanwai.Panel;

public sealed class LiveMessageEventArgs : EventArgs
{
    public LiveMessageEventArgs(
        string messageId,
        string transcript,
        string targetMessage,
        int skippedUntrusted,
        CapturePixelRect anchor,
        CapturePixelRect chatRegion)
    {
        MessageId = messageId;
        Transcript = transcript;
        TargetMessage = targetMessage;
        SkippedUntrusted = skippedUntrusted;
        Anchor = anchor;
        ChatRegion = chatRegion;
    }

    /// <summary>
    /// The observer's logical id for the bubble. Stays the same while it scrolls, so
    /// <see cref="LiveAnchorsEventArgs"/> can say where it is now.
    /// </summary>
    public string MessageId { get; }

    /// <summary>The bubble this judgment is about, in capture-frame pixels, when it arrived.</summary>
    public CapturePixelRect Anchor { get; }

    /// <summary>The chat area the bubble must stay inside to keep the HUD attached.</summary>
    public CapturePixelRect ChatRegion { get; }

    /// <summary>The recent conversation as "对方:" / "我:" lines, ending at the new message.</summary>
    public string Transcript { get; }

    public string TargetMessage { get; }

    /// <summary>How many recent messages were left out because OCR did not trust them.</summary>
    public int SkippedUntrusted { get; }
}

public sealed class LiveAnchorsEventArgs : EventArgs
{
    public LiveAnchorsEventArgs(
        IReadOnlyDictionary<string, CapturePixelRect> visibleBubbles,
        CapturePixelRect chatRegion,
        bool captureIsClean)
    {
        VisibleBubbles = visibleBubbles;
        ChatRegion = chatRegion;
        CaptureIsClean = captureIsClean;
    }

    /// <summary>Where each bubble on screen is in this frame, by logical message id.</summary>
    public IReadOnlyDictionary<string, CapturePixelRect> VisibleBubbles { get; }

    public CapturePixelRect ChatRegion { get; }

    /// <summary>
    /// False when the frame came from the visible-desktop fallback, which photographs
    /// whatever is on top of WeChat — including the HUD itself. Positions from such a
    /// frame must not be used to place the HUD.
    /// </summary>
    public bool CaptureIsClean { get; }
}

public sealed class LiveWindowEventArgs : EventArgs
{
    public LiveWindowEventArgs(WeChatWindowSnapshot? snapshot) => Snapshot = snapshot;

    public WeChatWindowSnapshot? Snapshot { get; }
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

    private readonly IChatRegionLocator _chatRegionLocator = new DarkThemeChatRegionLocator();
    private volatile object? _lastChatRegionBox;

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private bool _wasUnavailable;
    private bool _lastFrameWasFallback;
    private volatile bool _judgeLatestRequested;

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

    /// <summary>
    /// Raised once per polling tick with the current window state, or null when WeChat
    /// cannot be found. Drives the overlay's follow behaviour.
    /// </summary>
    public event EventHandler<LiveWindowEventArgs>? WindowObserved;

    /// <summary>
    /// Raised after every observed frame with the current position of each visible
    /// bubble. Drives the HUD's scroll behaviour.
    /// </summary>
    public event EventHandler<LiveAnchorsEventArgs>? AnchorsObserved;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// Asks for a judgment of the last remote message already on screen. Messages that
    /// were visible before live mode started are baseline and never fire on their own
    /// (GOTCHAS 4); this is the explicit way to judge one. Served on the next frame, on
    /// the loop thread, so the observer state is never read while it is being written.
    /// </summary>
    public void RequestJudgeLatest() => _judgeLatestRequested = true;

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
                WindowObserved?.Invoke(this, new LiveWindowEventArgs(window));
                if (window is null || !window.IsVisible || window.IsMinimized)
                {
                    if (!_wasUnavailable)
                    {
                        // A swallowed desktop-walk failure is the difference between
                        // "WeChat is minimized" and "something is broken and we will
                        // never find it". Say which.
                        var failure = _tracker.EnumerationFailure;
                        Report(failure is null
                            ? "暂停：微信不可见或已最小化。"
                            : $"暂停：找不到微信窗口，枚举时出错 — {failure}");
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

                    // The observer locates the chat region internally but does not
                    // report it, and the overlay needs it to know when a bubble has
                    // scrolled out. Locating again costs a few milliseconds.
                    var chatRegion = _chatRegionLocator.Locate(frame).Bounds;
                    _lastChatRegionBox = chatRegion;

                    var isFallback = frame.Method == CaptureMethod.VisibleDesktopFallback;
                    if (isFallback && !_lastFrameWasFallback)
                    {
                        // The first fallback frame may contain the HUD itself, which the
                        // detector could read as a bubble. Hide the HUD and drop this
                        // frame; the next one is taken with the HUD off screen.
                        _lastFrameWasFallback = true;
                        Report("截图退回到桌面拷贝（会拍到浮层），浮层暂停显示。");
                        AnchorsObserved?.Invoke(
                            this,
                            new LiveAnchorsEventArgs(
                                new Dictionary<string, CapturePixelRect>(),
                                chatRegion,
                                captureIsClean: false));
                    }
                    else
                    {
                        if (!isFallback && _lastFrameWasFallback)
                        {
                            Report("截图恢复为离屏绘制，浮层恢复。");
                        }

                        _lastFrameWasFallback = isFallback;
                        await _observer.ObserveAsync(frame, cancellationToken).ConfigureAwait(false);

                        var visible = new Dictionary<string, CapturePixelRect>(StringComparer.Ordinal);
                        foreach (var bubble in _observer.State.VisibleMessages)
                        {
                            visible[bubble.LogicalMessageId] = bubble.BubbleRect;
                        }

                        AnchorsObserved?.Invoke(
                            this,
                            new LiveAnchorsEventArgs(visible, chatRegion, captureIsClean: !isFallback));

                        if (_judgeLatestRequested)
                        {
                            _judgeLatestRequested = false;
                            JudgeLatestVisible();
                        }
                    }
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

    private void JudgeLatestVisible()
    {
        var state = _observer.State;
        var byId = state.Messages.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var remoteOnScreen = state.VisibleMessages
            .Where(bubble => bubble.Side == MessageSide.Remote)
            .OrderByDescending(bubble => bubble.BubbleRect.Y)
            .Select(bubble => byId.GetValueOrDefault(bubble.LogicalMessageId))
            .OfType<ObservedMessage>()
            .ToList();
        if (remoteOnScreen.Count == 0)
        {
            // Empty and "not ready yet" look the same from here; name both.
            Report("屏幕上没找到对方的消息（或会话基线还没建立），没有判定。");
            return;
        }

        // The newest bubble may be a sticker or a line OCR could not read. Judging it
        // anyway would put a verdict on text we do not have; walk up to the newest one
        // we can read, and say how many were passed over.
        var skipped = remoteOnScreen.TakeWhile(m => !m.IsTrustedForSemantics).Count();
        if (skipped == remoteOnScreen.Count)
        {
            Publish(remoteOnScreen[0]);
            return;
        }

        if (skipped > 0)
        {
            Report($"对方最新的 {skipped} 条没有可用文字（表情 / 图片 / 没认出来），改判再往上那条。");
        }

        Publish(remoteOnScreen[skipped]);
    }

    private void OnNewMessageObserved(object? sender, MessageObservedEventArgs e)
    {
        if (e.Message.Side != MessageSide.Remote)
        {
            return;
        }

        Publish(e.Message);
    }

    private void Publish(ObservedMessage message)
    {

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
                    $"这条消息的 OCR 不可信（{message.OcrStatus}），没有做判定。"));
            return;
        }

        var built = BuildTranscript(message);
        if (built is null)
        {
            return;
        }

        var chatRegion = _lastChatRegionBox is CapturePixelRect region
            ? region
            : message.BubbleRect;

        RemoteMessageArrived?.Invoke(
            this,
            new LiveMessageEventArgs(
                message.Id,
                built.Transcript,
                message.NormalizedText,
                built.SkippedUntrusted,
                message.BubbleRect,
                chatRegion));
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
