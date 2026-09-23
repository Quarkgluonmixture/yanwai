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
        CapturePixelRect chatRegion,
        bool isLive,
        bool targetVerified,
        int unverifiedInContext)
    {
        TargetVerified = targetVerified;
        UnverifiedInContext = unverifiedInContext;
        MessageId = messageId;
        IsLive = isLive;
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

    /// <summary>
    /// True for a message that just arrived; false for one already on screen that the
    /// user asked to have judged.
    /// </summary>
    public bool IsLive { get; }

    /// <summary>The bubble this judgment is about, in capture-frame pixels, when it arrived.</summary>
    public CapturePixelRect Anchor { get; }

    /// <summary>The chat area the bubble must stay inside to keep the HUD attached.</summary>
    public CapturePixelRect ChatRegion { get; }

    /// <summary>The recent conversation as "对方:" / "我:" lines, ending at the new message.</summary>
    public string Transcript { get; }

    public string TargetMessage { get; }

    /// <summary>How many recent messages were left out because OCR did not trust them.</summary>
    public int SkippedUntrusted { get; }

    /// <summary>
    /// False when the two OCR engines disagreed on the message being judged. The
    /// judgment still runs, and the text it ran on must be shown next to it.
    /// </summary>
    public bool TargetVerified { get; }

    /// <summary>How many context lines (target included) are unverified OCR readings.</summary>
    public int UnverifiedInContext { get; }
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

    /// <summary>One Jev call each; a screenful is rarely more than this.</summary>
    private const int MaxOnScreenJudgments = 8;

    private readonly int _intervalMilliseconds;
    private readonly LiveOcr _ocr;
    private readonly IMessageObserver _observer;
    private readonly Win32WeChatWindowTracker _tracker = new();
    private readonly Win32ScreenRegionCapture _capture = new();

    private readonly IChatRegionLocator _chatRegionLocator = new DarkThemeChatRegionLocator();
    private volatile object? _lastChatRegionBox;

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private bool _wasUnavailable;
    private bool _lastFrameWasFallback;
    private volatile bool _judgeScreenEnabled;

    /// <summary>Ids already handed to the panel. Loop thread only.</summary>
    private readonly HashSet<string> _published = new(StringComparer.Ordinal);

    public LiveConversationWatcher(string paddleModelDirectory, int intervalMilliseconds = 250)
    {
        _intervalMilliseconds = intervalMilliseconds;
        _ocr = LiveOcr.Create(paddleModelDirectory);

        _observer = new MessageObserver(
            new DarkThemeChatRegionLocator(),
            new DarkThemeBubbleDetector(),
            _ocr.Engine,
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
    /// From now on, judge every readable remote message that is on screen, including
    /// ones scrolled into view later. Messages that were visible before live mode
    /// started are baseline and never fire on their own (GOTCHAS 4); this is the
    /// explicit way to judge them. Served on the loop thread, so the observer state is
    /// never read while it is being written.
    /// </summary>
    public void EnableJudgeScreen() => _judgeScreenEnabled = true;

    /// <summary>The observer started a new conversation; earlier message ids are gone.</summary>
    public event EventHandler? ConversationReset;

    /// <summary>The pinned PP-OCR model near the binary, or null when it is not installed.</summary>
    public static string? FindOcrModel() => LiveOcr.FindModelDirectory(AppContext.BaseDirectory);

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

                        if (_judgeScreenEnabled)
                        {
                            JudgeScreen();
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
        ConversationReset?.Invoke(this, EventArgs.Empty);
        Report(e.PreviousEpoch is null
            ? "已建立当前会话基线。"
            : $"检测到会话切换（{e.PreviousEpoch.Id} → {e.CurrentEpoch.Id}），重新建立基线。");
    }

    /// <summary>Runs every frame while enabled; only messages not yet handed over go out.</summary>
    private void JudgeScreen()
    {
        var state = _observer.State;
        var byId = state.Messages.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var fresh = state.VisibleMessages
            .Where(bubble => bubble.Side == MessageSide.Remote && !_published.Contains(bubble.LogicalMessageId))
            .OrderByDescending(bubble => bubble.BubbleRect.Y)
            .Select(bubble => byId.GetValueOrDefault(bubble.LogicalMessageId))
            .OfType<ObservedMessage>()
            .ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        // Bubbles with no text at all (stickers, images) are passed over and marked as
        // handled, so they are counted once instead of every frame. Unverified readings
        // are judged and labelled as such downstream.
        var readable = fresh.Where(HasReadableText).Take(MaxOnScreenJudgments).ToList();
        var textless = fresh.Count(m => !HasReadableText(m));
        foreach (var message in fresh.Where(m => !HasReadableText(m)))
        {
            _published.Add(message.Id);
        }

        if (textless > 0)
        {
            Report($"屏幕上又有对方 {fresh.Count} 条，{textless} 条没有文字（表情 / 图片），跳过。");
        }

        // Newest first, so the one most likely to need an answer comes back first.
        foreach (var message in readable)
        {
            Publish(message, isLive: false);
        }
    }

    private void OnNewMessageObserved(object? sender, MessageObservedEventArgs e)
    {
        if (e.Message.Side != MessageSide.Remote)
        {
            return;
        }

        Publish(e.Message, isLive: true);
    }

    private void Publish(ObservedMessage message, bool isLive)
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

        if (!HasReadableText(message))
        {
            RemoteMessageSkipped?.Invoke(
                this,
                new LiveSkippedEventArgs(
                    "[没认出来]",
                    $"这条消息没读出文字（{message.OcrStatus}），没有做判定。"));
            return;
        }

        var built = BuildTranscript(message);
        if (built is null)
        {
            return;
        }

        _published.Add(message.Id);

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
                chatRegion,
                isLive,
                message.IsTrustedForSemantics,
                built.UnverifiedCount));
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
            HasReadableText(message),
            message.OcrStatus == OcrTextStatus.NoText,
            IsVerified: message.IsTrustedForSemantics);

    /// <summary>
    /// Text is usable for a judgment when OCR produced some, verified or not. Leaving
    /// unverified readings out made most of a real screen disappear (the checker engine
    /// is the weaker one); showing the text that was judged lets the user see a misread.
    /// </summary>
    private static bool HasReadableText(ObservedMessage message) =>
        message.OcrStatus is OcrTextStatus.Recognized or OcrTextStatus.LowConfidence &&
        !string.IsNullOrWhiteSpace(message.NormalizedText);

    private void Report(string message) =>
        StatusChanged?.Invoke(this, new LiveStatusEventArgs(message));

    public void Dispose()
    {
        _observer.NewMessageObserved -= OnNewMessageObserved;
        _observer.ConversationChanged -= OnConversationChanged;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _ocr.Dispose();
    }
}
