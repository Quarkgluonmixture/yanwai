using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Ocr;
using WeChatJevHud.Observer;
using WeChatJevHud.Vision;

namespace WeChatJevHud.Observer.Tests;

public sealed class MessageObserverTests
{
    private static readonly CapturePixelRect ChatRegion = new(0, 20, 200, 180);

    [Fact]
    public async Task Stable_frame_bootstraps_once_without_repeating_detection_or_ocr()
    {
        var frame = Frame(headerValue: 10, bubbleValue: 80);
        var detector = new StubBubbleDetector(
            [new DetectedBubble(new CapturePixelRect(12, 60, 50, 24), MessageSide.Remote, 0.94)]);
        var ocr = new StubOcrEngine(new OcrResult("已有消息", 0.96, OcrTextStatus.Recognized, "已有消息"));
        var observer = CreateObserver(detector, ocr);

        var first = await observer.ObserveAsync(frame, CancellationToken.None);
        var second = await observer.ObserveAsync(frame, CancellationToken.None);

        var bootstrap = Assert.Single(first.MessagesObserved);
        Assert.Equal(MessageObservationKind.Bootstrap, bootstrap.Origin);
        Assert.Empty(first.NewMessages);
        Assert.False(second.FrameChanged);
        Assert.Empty(second.MessagesObserved);
        Assert.Empty(second.NewMessages);
        Assert.Equal(1, detector.Calls);
        Assert.Equal(1, ocr.Calls);
        Assert.Equal(2, second.Counters.FramesChecked);
        Assert.Equal(1, second.Counters.UnchangedFrames);
        Assert.Equal(1, second.Counters.BubbleDetectionRuns);
        Assert.Equal(1, second.Counters.OcrCalls);
        Assert.Equal(0, second.Counters.MessagesEmitted);
    }

    [Fact]
    public async Task Appending_one_remote_message_emits_it_once_and_only_ocrs_the_new_crop()
    {
        var existing = new DetectedBubble(new CapturePixelRect(12, 54, 50, 24), MessageSide.Remote, 0.94);
        var appended = new DetectedBubble(new CapturePixelRect(12, 100, 70, 24), MessageSide.Remote, 0.96);
        var firstFrame = Frame(
            headerValue: 10,
            [(existing.Bounds, (byte)80)]);
        var secondFrame = Frame(
            headerValue: 10,
            [(existing.Bounds, (byte)80), (appended.Bounds, (byte)120)]);
        var detector = new StubBubbleDetector([existing], [existing, appended]);
        var ocr = new StubOcrEngine(
            new OcrResult("已有消息", 0.96, OcrTextStatus.Recognized, "已有消息"),
            new OcrResult("hello-1", 0.98, OcrTextStatus.Recognized, "hello-1"));
        var observer = CreateObserver(detector, ocr);

        await observer.ObserveAsync(firstFrame, CancellationToken.None);
        var changed = await observer.ObserveAsync(secondFrame, CancellationToken.None);
        var stable = await observer.ObserveAsync(secondFrame, CancellationToken.None);

        var message = Assert.Single(changed.NewMessages);
        Assert.Equal(MessageObservationKind.LiveNew, message.Origin);
        Assert.Equal(MessageSide.Remote, message.Side);
        Assert.Equal("hello-1", message.NormalizedText);
        Assert.True(message.IsTrustedForSemantics);
        Assert.Equal(2, ocr.Calls);
        Assert.Equal(2, detector.Calls);
        Assert.Equal(1, changed.Counters.MessagesEmitted);
        Assert.Empty(stable.NewMessages);
        Assert.Equal(2, observer.State.Messages.Count);
    }

    [Fact]
    public async Task Appending_one_self_message_updates_state_and_event_stream_once()
    {
        var existing = new DetectedBubble(new CapturePixelRect(12, 54, 50, 24), MessageSide.Remote, 0.94);
        var appended = new DetectedBubble(new CapturePixelRect(120, 100, 60, 24), MessageSide.Self, 0.96);
        var detector = new StubBubbleDetector([existing], [existing, appended]);
        var ocr = new StubOcrEngine(
            new OcrResult("已有消息", 0.96, OcrTextStatus.Recognized, "已有消息"),
            new OcrResult("收到", 0.98, OcrTextStatus.Recognized, "收到"));
        var observer = CreateObserver(detector, ocr);
        var emitted = new List<ObservedMessage>();
        observer.NewMessageObserved += (_, args) => emitted.Add(args.Message);

        await observer.ObserveAsync(
            Frame(10, [(existing.Bounds, (byte)80)]),
            CancellationToken.None);
        var result = await observer.ObserveAsync(
            Frame(10, [(existing.Bounds, (byte)80), (appended.Bounds, (byte)140)]),
            CancellationToken.None);

        var message = Assert.Single(result.NewMessages);
        Assert.Equal(MessageSide.Self, message.Side);
        Assert.Equal(message.Id, Assert.Single(emitted).Id);
        Assert.Equal(2, observer.State.Messages.Count);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Repeated_identical_remote_text_produces_two_logical_messages()
    {
        var existing = new DetectedBubble(new CapturePixelRect(12, 45, 50, 24), MessageSide.Remote, 0.94);
        var firstHao = new DetectedBubble(new CapturePixelRect(12, 85, 32, 24), MessageSide.Remote, 0.96);
        var secondHao = new DetectedBubble(new CapturePixelRect(12, 125, 32, 24), MessageSide.Remote, 0.96);
        var detector = new StubBubbleDetector(
            [existing],
            [existing, firstHao],
            [existing, firstHao, secondHao]);
        var ocr = new StubOcrEngine(
            new OcrResult("已有消息", 0.96, OcrTextStatus.Recognized, "已有消息"),
            new OcrResult("好", 0.99, OcrTextStatus.Recognized, "好"),
            new OcrResult("好", 0.99, OcrTextStatus.Recognized, "好"));
        var observer = CreateObserver(detector, ocr);

        await observer.ObserveAsync(Frame(10, [(existing.Bounds, (byte)80)]), CancellationToken.None);
        var first = await observer.ObserveAsync(
            Frame(10, [(existing.Bounds, (byte)80), (firstHao.Bounds, (byte)120)]),
            CancellationToken.None);
        var second = await observer.ObserveAsync(
            Frame(
                10,
                [(existing.Bounds, (byte)80), (firstHao.Bounds, (byte)120), (secondHao.Bounds, (byte)120)]),
            CancellationToken.None);

        var firstMessage = Assert.Single(first.NewMessages);
        var secondMessage = Assert.Single(second.NewMessages);
        Assert.Equal("好", firstMessage.NormalizedText);
        Assert.Equal("好", secondMessage.NormalizedText);
        Assert.NotEqual(firstMessage.Id, secondMessage.Id);
        Assert.Equal(3, ocr.Calls);
        Assert.Equal(2, second.Counters.MessagesEmitted);
    }

    [Fact]
    public async Task Identical_text_on_opposite_sides_remains_two_messages()
    {
        var self = new DetectedBubble(new CapturePixelRect(145, 60, 32, 24), MessageSide.Self, 0.96);
        var remote = new DetectedBubble(new CapturePixelRect(12, 100, 32, 24), MessageSide.Remote, 0.96);
        var detector = new StubBubbleDetector([self], [self, remote]);
        var ocr = new StubOcrEngine(
            new OcrResult("嗯", 0.99, OcrTextStatus.Recognized, "嗯"),
            new OcrResult("嗯", 0.99, OcrTextStatus.Recognized, "嗯"));
        var observer = CreateObserver(detector, ocr);

        var bootstrap = await observer.ObserveAsync(
            Frame(10, [(self.Bounds, (byte)130)]),
            CancellationToken.None);
        var live = await observer.ObserveAsync(
            Frame(10, [(self.Bounds, (byte)130), (remote.Bounds, (byte)130)]),
            CancellationToken.None);

        var selfMessage = Assert.Single(bootstrap.MessagesObserved);
        var remoteMessage = Assert.Single(live.NewMessages);
        Assert.Equal(MessageSide.Self, selfMessage.Side);
        Assert.Equal(MessageSide.Remote, remoteMessage.Side);
        Assert.Equal("嗯", selfMessage.NormalizedText);
        Assert.Equal("嗯", remoteMessage.NormalizedText);
        Assert.NotEqual(selfMessage.Id, remoteMessage.Id);
    }

    [Fact]
    public async Task Scrolling_existing_history_does_not_emit_old_messages()
    {
        var a = Bubble(12, 45, 60, MessageSide.Remote);
        var b = Bubble(12, 80, 70, MessageSide.Remote);
        var cBottom = Bubble(12, 115, 80, MessageSide.Remote);
        var cTop = Bubble(12, 45, 80, MessageSide.Remote);
        var d = Bubble(12, 80, 90, MessageSide.Remote);
        var e = Bubble(12, 115, 100, MessageSide.Remote);
        var detector = new StubBubbleDetector(
            [cTop, d, e],
            [a, b, cBottom],
            [cTop, d, e]);
        var ocr = new StubOcrEngine(
            Ocr("C"), Ocr("D"), Ocr("E"),
            Ocr("A"), Ocr("B"));
        var observer = CreateObserver(detector, ocr);

        await observer.ObserveAsync(
            Frame(10, [(cTop.Bounds, (byte)80), (d.Bounds, (byte)90), (e.Bounds, (byte)100)]),
            CancellationToken.None);
        var scrolledUp = await observer.ObserveAsync(
            Frame(10, [(a.Bounds, (byte)60), (b.Bounds, (byte)70), (cBottom.Bounds, (byte)80)]),
            CancellationToken.None);
        var returned = await observer.ObserveAsync(
            Frame(10, [(cTop.Bounds, (byte)80), (d.Bounds, (byte)90), (e.Bounds, (byte)100)]),
            CancellationToken.None);

        Assert.Empty(scrolledUp.NewMessages);
        Assert.All(scrolledUp.MessagesObserved, message => Assert.Equal(MessageObservationKind.History, message.Origin));
        Assert.Empty(returned.NewMessages);
        Assert.Empty(returned.MessagesObserved);
        Assert.Equal(5, ocr.Calls);
        Assert.Equal(5, observer.State.Messages.Count);
    }

    [Fact]
    public async Task Scrolling_away_and_returning_does_not_replay_a_live_message()
    {
        var a = Bubble(12, 45, 60, MessageSide.Remote);
        var bBottom = Bubble(12, 80, 70, MessageSide.Remote);
        var bTop = Bubble(12, 45, 70, MessageSide.Remote);
        var cBottom = Bubble(12, 115, 80, MessageSide.Remote);
        var cMiddle = Bubble(12, 80, 80, MessageSide.Remote);
        var d = Bubble(12, 115, 90, MessageSide.Remote);
        var detector = new StubBubbleDetector(
            [bBottom, cBottom],
            [bTop, cMiddle, d],
            [a, bBottom, cBottom],
            [bTop, cMiddle, d]);
        var ocr = new StubOcrEngine(Ocr("B"), Ocr("C"), Ocr("D"), Ocr("A"));
        var observer = CreateObserver(detector, ocr);

        await observer.ObserveAsync(
            Frame(10, [(bBottom.Bounds, (byte)70), (cBottom.Bounds, (byte)80)]),
            CancellationToken.None);
        var appended = await observer.ObserveAsync(
            Frame(10, [(bTop.Bounds, (byte)70), (cMiddle.Bounds, (byte)80), (d.Bounds, (byte)90)]),
            CancellationToken.None);
        var away = await observer.ObserveAsync(
            Frame(10, [(a.Bounds, (byte)60), (bBottom.Bounds, (byte)70), (cBottom.Bounds, (byte)80)]),
            CancellationToken.None);
        var returned = await observer.ObserveAsync(
            Frame(10, [(bTop.Bounds, (byte)70), (cMiddle.Bounds, (byte)80), (d.Bounds, (byte)90)]),
            CancellationToken.None);

        Assert.Equal("D", Assert.Single(appended.NewMessages).NormalizedText);
        Assert.Empty(away.NewMessages);
        Assert.Empty(returned.NewMessages);
        Assert.Empty(returned.MessagesObserved);
        Assert.Equal(1, returned.Counters.MessagesEmitted);
        Assert.Equal(4, ocr.Calls);
    }

    [Fact]
    public async Task Conversation_switch_creates_a_new_epoch_and_bootstraps_without_state_leakage()
    {
        var firstBubble = Bubble(12, 60, 80, MessageSide.Remote);
        var secondBubble = Bubble(12, 60, 120, MessageSide.Self);
        var detector = new StubBubbleDetector([firstBubble], [secondBubble]);
        var ocr = new StubOcrEngine(Ocr("chat-one"), Ocr("chat-two"));
        var observer = CreateObserver(detector, ocr);
        var epochEvents = new List<ConversationChangedEventArgs>();
        observer.ConversationChanged += (_, args) => epochEvents.Add(args);

        var first = await observer.ObserveAsync(
            Frame(10, [(firstBubble.Bounds, (byte)80)]),
            CancellationToken.None);
        var switched = await observer.ObserveAsync(
            Frame(30, [(secondBubble.Bounds, (byte)120)]),
            CancellationToken.None);

        Assert.Equal(1, first.Epoch.Id);
        Assert.Equal(2, switched.Epoch.Id);
        var bootstrap = Assert.Single(switched.MessagesObserved);
        Assert.Equal(MessageObservationKind.Bootstrap, bootstrap.Origin);
        Assert.Equal("chat-two", bootstrap.NormalizedText);
        Assert.Empty(switched.NewMessages);
        Assert.DoesNotContain(observer.State.Messages, message => message.NormalizedText == "chat-one");
        Assert.Equal(1, switched.Counters.ConversationSwitches);
        Assert.Equal(2, epochEvents.Count);
        Assert.Null(epochEvents[0].PreviousEpoch);
        Assert.Equal(first.Epoch.Id, epochEvents[1].PreviousEpoch!.Id);
    }

    [Fact]
    public async Task Restore_after_capture_suspension_does_not_replay_visible_messages()
    {
        var bubble = Bubble(12, 60, 80, MessageSide.Remote);
        var frame = Frame(10, [(bubble.Bounds, (byte)80)]);
        var detector = new StubBubbleDetector([bubble]);
        var ocr = new StubOcrEngine(Ocr("visible-before-minimize"));
        var observer = CreateObserver(detector, ocr);

        await observer.ObserveAsync(frame, CancellationToken.None);
        // A minimized window supplies no frame to the observer. Restoring resumes with
        // the next valid capture while retaining the in-memory reconciliation state.
        var restored = await observer.ObserveAsync(frame, CancellationToken.None);

        Assert.False(restored.FrameChanged);
        Assert.Empty(restored.MessagesObserved);
        Assert.Empty(restored.NewMessages);
        Assert.Equal(1, detector.Calls);
        Assert.Equal(1, ocr.Calls);
        Assert.Equal(0, restored.Counters.MessagesEmitted);
    }

    [Fact]
    public async Task Low_confidence_message_is_preserved_but_not_trusted_for_semantics()
    {
        var existing = Bubble(12, 55, 80, MessageSide.Remote);
        var uncertain = Bubble(12, 100, 120, MessageSide.Remote);
        var detector = new StubBubbleDetector([existing], [existing, uncertain]);
        var ocr = new StubOcrEngine(
            Ocr("existing"),
            new OcrResult("怎久说", 0.72, OcrTextStatus.LowConfidence, "怎久说\n"));
        var observer = CreateObserver(detector, ocr);

        await observer.ObserveAsync(
            Frame(10, [(existing.Bounds, (byte)80)]),
            CancellationToken.None);
        var result = await observer.ObserveAsync(
            Frame(10, [(existing.Bounds, (byte)80), (uncertain.Bounds, (byte)120)]),
            CancellationToken.None);

        var message = Assert.Single(result.NewMessages);
        Assert.Equal("怎久说", message.NormalizedText);
        Assert.Equal("怎久说\n", message.RawText);
        Assert.Equal(OcrTextStatus.LowConfidence, message.OcrStatus);
        Assert.Equal(0.72, message.OcrConfidence);
        Assert.False(message.IsTrustedForSemantics);
        Assert.Contains(observer.State.Messages, item => item.Id == message.Id);
    }

    [Fact]
    public async Task Recent_conversation_state_enforces_the_configured_message_limit()
    {
        var first = Bubble(12, 45, 60, MessageSide.Remote);
        var second = Bubble(12, 85, 80, MessageSide.Remote);
        var third = Bubble(12, 125, 100, MessageSide.Self);
        var detector = new StubBubbleDetector([first], [first, second], [first, second, third]);
        var ocr = new StubOcrEngine(Ocr("one"), Ocr("two"), Ocr("three"));
        var observer = CreateObserver(
            detector,
            ocr,
            new ObserverOptions(RecentMessageLimit: 2));

        await observer.ObserveAsync(Frame(10, [(first.Bounds, (byte)60)]), CancellationToken.None);
        await observer.ObserveAsync(
            Frame(10, [(first.Bounds, (byte)60), (second.Bounds, (byte)80)]),
            CancellationToken.None);
        await observer.ObserveAsync(
            Frame(10, [(first.Bounds, (byte)60), (second.Bounds, (byte)80), (third.Bounds, (byte)100)]),
            CancellationToken.None);

        Assert.Equal(["two", "three"], observer.State.Messages.Select(message => message.NormalizedText));
    }

    [Fact]
    public async Task First_message_after_an_empty_bootstrap_is_emitted_as_live_new()
    {
        var firstMessage = Bubble(12, 60, 80, MessageSide.Remote);
        var detector = new StubBubbleDetector([], [firstMessage]);
        var ocr = new StubOcrEngine(Ocr("first-live-message"));
        var observer = CreateObserver(detector, ocr);

        var emptyBaseline = await observer.ObserveAsync(Frame(10, []), CancellationToken.None);
        var appended = await observer.ObserveAsync(
            Frame(10, [(firstMessage.Bounds, (byte)80)]),
            CancellationToken.None);

        Assert.Empty(emptyBaseline.MessagesObserved);
        Assert.Equal("first-live-message", Assert.Single(appended.NewMessages).NormalizedText);
        Assert.Equal(1, appended.Counters.MessagesEmitted);
    }

    [Fact]
    public async Task Changed_visual_crop_with_matching_side_and_text_keeps_the_logical_identity()
    {
        var bubble = Bubble(12, 60, 80, MessageSide.Remote);
        var detector = new StubBubbleDetector([bubble], [bubble]);
        var ocr = new StubOcrEngine(Ocr("same-message"), Ocr("same-message"));
        var observer = CreateObserver(detector, ocr);

        var baseline = await observer.ObserveAsync(
            Frame(10, [(bubble.Bounds, (byte)80)]),
            CancellationToken.None);
        var changedRendering = await observer.ObserveAsync(
            Frame(10, [(bubble.Bounds, (byte)120)]),
            CancellationToken.None);

        var original = Assert.Single(baseline.MessagesObserved);
        Assert.Empty(changedRendering.MessagesObserved);
        Assert.Empty(changedRendering.NewMessages);
        Assert.Equal(original.Id, Assert.Single(observer.State.VisibleMessages).LogicalMessageId);
        Assert.Equal(2, ocr.Calls);
        Assert.Equal(1, changedRendering.Counters.DuplicatesSuppressed);
    }

    [Fact]
    public async Task Scrolling_an_all_identical_sequence_suppresses_the_ambiguous_extra_bubble()
    {
        var first = Bubble(12, 45, 120, MessageSide.Remote);
        var second = Bubble(12, 85, 120, MessageSide.Remote);
        var third = Bubble(12, 125, 120, MessageSide.Remote);
        var detector = new StubBubbleDetector([first, second], [first, second, third]);
        var ocr = new StubOcrEngine(Ocr("好"), Ocr("好"), Ocr("好"));
        var observer = CreateObserver(detector, ocr);

        await observer.ObserveAsync(
            Frame(10, [(first.Bounds, (byte)120), (second.Bounds, (byte)120)]),
            CancellationToken.None);
        var ambiguousScroll = await observer.ObserveAsync(
            Frame(10, [(first.Bounds, (byte)120), (second.Bounds, (byte)120), (third.Bounds, (byte)120)]),
            CancellationToken.None);

        Assert.Empty(ambiguousScroll.NewMessages);
        Assert.Equal(MessageObservationKind.History, Assert.Single(ambiguousScroll.MessagesObserved).Origin);
        Assert.Equal(0, ambiguousScroll.Counters.MessagesEmitted);
    }

    private static IMessageObserver CreateObserver(
        IBubbleDetector detector,
        IOcrEngine ocr,
        ObserverOptions? options = null) =>
        new MessageObserver(
            new StubChatRegionLocator(),
            detector,
            ocr,
            new ChatRoiChangeDetector(),
            new VisualConversationIdentityProvider(),
            options);

    private static DetectedBubble Bubble(int x, int y, byte value, MessageSide side) =>
        new(new CapturePixelRect(x, y, 40, 24), side, value / 255d);

    private static OcrResult Ocr(string text) =>
        new(text, 0.98, OcrTextStatus.Recognized, text);

    private static CapturedFrame Frame(byte headerValue, byte bubbleValue) =>
        Frame(headerValue, [(new CapturePixelRect(12, 60, 50, 24), bubbleValue)]);

    private static CapturedFrame Frame(
        byte headerValue,
        IReadOnlyList<(CapturePixelRect Rect, byte Value)> bubbles)
    {
        const int width = 200;
        const int height = 200;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Fill(pixels, stride, new CapturePixelRect(0, 0, width, 20), headerValue);
        foreach (var bubble in bubbles)
        {
            Fill(pixels, stride, bubble.Rect, bubble.Value);
        }
        return new CapturedFrame(
            width,
            height,
            stride,
            pixels,
            new DesktopPixelRect(0, 0, width, height),
            CaptureMethod.RenderWindow,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(1));
    }

    private static void Fill(byte[] pixels, int stride, CapturePixelRect rect, byte value)
    {
        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            for (var x = rect.X; x < rect.Right; x++)
            {
                var offset = (y * stride) + (x * 4);
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }
    }

    private sealed class StubChatRegionLocator : IChatRegionLocator
    {
        public DetectedChatRegion Locate(CapturedFrame frame) => new(ChatRegion, 1);
    }

    private sealed class StubBubbleDetector(params IReadOnlyList<DetectedBubble>[] frames) : IBubbleDetector
    {
        public int Calls { get; private set; }

        public IReadOnlyList<DetectedBubble> Detect(CapturedFrame frame, CapturePixelRect chatRegion)
        {
            var index = Math.Min(Calls, frames.Length - 1);
            Calls++;
            return frames[index];
        }
    }

    private sealed class StubOcrEngine(params OcrResult[] results) : IOcrEngine
    {
        public string Name => "stub";

        public int Calls { get; private set; }

        public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
        {
            var index = Math.Min(Calls, results.Length - 1);
            Calls++;
            return Task.FromResult(results[index]);
        }
    }
}
