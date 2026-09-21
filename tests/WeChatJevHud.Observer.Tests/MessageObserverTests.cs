using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Ocr;
using WeChatJevHud.Observer;
using WeChatJevHud.Vision;

namespace WeChatJevHud.Observer.Tests;

public sealed class MessageObserverTests
{

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
        var detector = new StubBubbleDetector([firstBubble], [secondBubble], [secondBubble], [secondBubble]);
        var ocr = new StubOcrEngine(Ocr("chat-one"), Ocr("chat-two"));
        var identity = new StubConversationIdentityProvider(
            Identity(1),
            Identity(2),
            Identity(2),
            Identity(2),
            Identity(2),
            Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);
        var epochEvents = new List<ConversationChangedEventArgs>();
        observer.ConversationChanged += (_, args) => epochEvents.Add(args);

        var first = await observer.ObserveAsync(
            Frame(10, [(firstBubble.Bounds, (byte)80)]),
            CancellationToken.None);
        var firstCandidate = await observer.ObserveAsync(
            Frame(30, [(secondBubble.Bounds, (byte)120)]),
            CancellationToken.None);
        var secondCandidate = await observer.ObserveAsync(
            Frame(30, [(secondBubble.Bounds, (byte)120)]),
            CancellationToken.None);
        var switched = await observer.ObserveAsync(
            Frame(30, [(secondBubble.Bounds, (byte)120)]),
            CancellationToken.None);
        var remained = await observer.ObserveAsync(
            Frame(30, [(secondBubble.Bounds, (byte)120)]),
            CancellationToken.None);
        var remainedAgain = await observer.ObserveAsync(
            Frame(30, [(secondBubble.Bounds, (byte)120)]),
            CancellationToken.None);

        Assert.Equal(1, first.Epoch.Id);
        Assert.Equal(1, firstCandidate.Epoch.Id);
        Assert.Equal(1, secondCandidate.Epoch.Id);
        Assert.Equal(ConversationIdentityDecision.PendingSwitch, firstCandidate.Identity.Decision);
        Assert.Equal(1, firstCandidate.Identity.PendingObservations);
        Assert.Equal(ConversationIdentityDecision.PendingSwitch, secondCandidate.Identity.Decision);
        Assert.Equal(2, secondCandidate.Identity.PendingObservations);
        Assert.Empty(firstCandidate.MessagesObserved);
        Assert.Empty(secondCandidate.MessagesObserved);
        Assert.Equal(2, switched.Epoch.Id);
        Assert.Equal(ConversationIdentityDecision.ConfirmedSwitch, switched.Identity.Decision);
        var bootstrap = Assert.Single(switched.MessagesObserved);
        Assert.Equal(MessageObservationKind.Bootstrap, bootstrap.Origin);
        Assert.Equal("chat-two", bootstrap.NormalizedText);
        Assert.Empty(switched.NewMessages);
        Assert.DoesNotContain(observer.State.Messages, message => message.NormalizedText == "chat-one");
        Assert.Equal(1, switched.Counters.ConversationSwitches);
        Assert.Equal(2, epochEvents.Count);
        Assert.Null(epochEvents[0].PreviousEpoch);
        Assert.Equal(first.Epoch.Id, epochEvents[1].PreviousEpoch!.Id);
        Assert.Equal(2, ocr.Calls);
        Assert.Equal(switched.Epoch.Id, remained.Epoch.Id);
        Assert.Equal(switched.Epoch.Id, remainedAgain.Epoch.Id);
        Assert.Equal(1, remainedAgain.Counters.ConversationSwitches);
        Assert.Equal(1, remainedAgain.Counters.IdentitySwitchesConfirmed);
    }

    [Fact]
    public async Task Same_conversation_with_changed_header_rendering_keeps_the_epoch()
    {
        var bubble = Bubble(12, 60, 80, MessageSide.Remote);
        var detector = new StubBubbleDetector([bubble], [bubble]);
        var ocr = new StubOcrEngine(Ocr("same-message"));
        var observer = CreateObserver(detector, ocr);

        var baseline = await observer.ObserveAsync(
            Frame(10, [(bubble.Bounds, (byte)80)]),
            CancellationToken.None);
        var rerendered = await observer.ObserveAsync(
            Frame(30, [(bubble.Bounds, (byte)80)]),
            CancellationToken.None);

        Assert.Equal(baseline.Epoch.Id, rerendered.Epoch.Id);
        Assert.Empty(rerendered.MessagesObserved);
        Assert.Empty(rerendered.NewMessages);
        Assert.Equal(0, rerendered.Counters.ConversationSwitches);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task Dynamic_right_side_header_controls_do_not_change_conversation_identity()
    {
        var bubble = Bubble(12, 60, 80, MessageSide.Remote);
        var baselineFrame = PatternedFrame(200, 200, bubble.Bounds, 80);
        var controlChangedFrame = CloneWithFill(
            baselineFrame,
            new CapturePixelRect(170, 3, 24, 12),
            220);
        var detector = new StubBubbleDetector([bubble]);
        var ocr = new StubOcrEngine(Ocr("same-message"));
        var observer = CreateObserver(detector, ocr);

        var baseline = await observer.ObserveAsync(baselineFrame, CancellationToken.None);
        var controlChanged = await observer.ObserveAsync(controlChangedFrame, CancellationToken.None);

        Assert.Equal(baseline.Epoch.Id, controlChanged.Epoch.Id);
        Assert.False(controlChanged.FrameChanged);
        Assert.Equal(0, controlChanged.Counters.IdentityMismatchCandidates);
        Assert.Equal(1, detector.Calls);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task Changed_header_with_two_strong_previous_visible_matches_rebases_the_same_epoch()
    {
        var first = Bubble(12, 55, 80, MessageSide.Remote);
        var second = Bubble(120, 95, 120, MessageSide.Self);
        var detector = new StubBubbleDetector([first, second], [first, second]);
        var ocr = new StubOcrEngine(Ocr("first"), Ocr("second"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        var baseline = await observer.ObserveAsync(
            Frame(10, [(first.Bounds, (byte)80), (second.Bounds, (byte)120)]),
            CancellationToken.None);
        var rerendered = await observer.ObserveAsync(
            Frame(30, [(first.Bounds, (byte)80), (second.Bounds, (byte)120)]),
            CancellationToken.None);

        Assert.Equal(baseline.Epoch.Id, rerendered.Epoch.Id);
        Assert.Empty(rerendered.MessagesObserved);
        Assert.Empty(rerendered.NewMessages);
        Assert.Equal(0, rerendered.Counters.ConversationSwitches);
        Assert.Equal(ConversationIdentityDecision.RebaseSameConversation, rerendered.Identity.Decision);
        Assert.Equal(2, rerendered.Identity.PreviousVisibleStrongOverlap);
        Assert.Equal(1, rerendered.Counters.IdentityRebases);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Different_conversation_with_two_weak_visual_matches_enters_pending_switch()
    {
        var first = Bubble(12, 55, 80, MessageSide.Remote);
        var second = Bubble(120, 95, 100, MessageSide.Self);
        var detector = new StubBubbleDetector([first, second], [first, second]);
        var ocr = new StubOcrEngine(
            Ocr("chat-a-first"),
            Ocr("chat-a-second"),
            Ocr("chat-b-first"),
            Ocr("chat-b-second"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        var baseline = await observer.ObserveAsync(
            Frame(10, [(first.Bounds, (byte)80), (second.Bounds, (byte)100)]),
            CancellationToken.None);
        var candidate = await observer.ObserveAsync(
            Frame(30, [(first.Bounds, (byte)86), (second.Bounds, (byte)106)]),
            CancellationToken.None);

        Assert.Equal(baseline.Epoch.Id, candidate.Epoch.Id);
        Assert.Equal(ConversationIdentityDecision.PendingSwitch, candidate.Identity.Decision);
        Assert.Equal(1, candidate.Identity.PendingObservations);
        Assert.Empty(candidate.MessagesObserved);
        Assert.Empty(candidate.NewMessages);
        Assert.Equal(0, candidate.Identity.PreviousVisibleStrongOverlap);
        Assert.Equal(2, candidate.Identity.PreviousVisibleWeakOverlap);
        Assert.Equal(0, candidate.Identity.TrustedTextOverlap);
        Assert.False(candidate.Identity.LiveTailStrongMatch);
        Assert.True(candidate.Identity.LiveTailWeakMatch);
        Assert.Equal(4, ocr.Calls);
    }

    [Fact]
    public async Task Trusted_text_overlap_on_previous_visible_messages_is_strong_continuity()
    {
        var first = Bubble(12, 55, 80, MessageSide.Remote);
        var second = Bubble(120, 95, 100, MessageSide.Self);
        var detector = new StubBubbleDetector([first, second], [first, second]);
        var ocr = new StubOcrEngine(
            Ocr("stable-first"),
            Ocr("stable-second"),
            Ocr("stable-first"),
            Ocr("stable-second"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        var baseline = await observer.ObserveAsync(
            Frame(10, [(first.Bounds, (byte)80), (second.Bounds, (byte)100)]),
            CancellationToken.None);
        var rerendered = await observer.ObserveAsync(
            Frame(30, [(first.Bounds, (byte)86), (second.Bounds, (byte)106)]),
            CancellationToken.None);

        Assert.Equal(baseline.Epoch.Id, rerendered.Epoch.Id);
        Assert.Equal(ConversationIdentityDecision.RebaseSameConversation, rerendered.Identity.Decision);
        Assert.Equal(2, rerendered.Identity.PreviousVisibleStrongOverlap);
        Assert.Equal(0, rerendered.Identity.PreviousVisibleWeakOverlap);
        Assert.Equal(2, rerendered.Identity.TrustedTextOverlap);
        Assert.True(rerendered.Identity.LiveTailStrongMatch);
        Assert.Empty(rerendered.NewMessages);
        Assert.Equal(4, ocr.Calls);
    }

    [Fact]
    public async Task Low_confidence_text_overlap_is_not_strong_continuity()
    {
        var bubble = Bubble(12, 60, 80, MessageSide.Remote);
        var detector = new StubBubbleDetector([bubble], [bubble]);
        var ocr = new StubOcrEngine(
            new OcrResult("same-text", 0.45, OcrTextStatus.LowConfidence, "same-text"),
            Ocr("same-text"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        await observer.ObserveAsync(
            Frame(10, [(bubble.Bounds, (byte)80)]),
            CancellationToken.None);
        var candidate = await observer.ObserveAsync(
            Frame(30, [(bubble.Bounds, (byte)86)]),
            CancellationToken.None);

        Assert.Equal(ConversationIdentityDecision.PendingSwitch, candidate.Identity.Decision);
        Assert.Equal(0, candidate.Identity.TrustedTextOverlap);
        Assert.False(candidate.Identity.LiveTailStrongMatch);
        Assert.True(candidate.Identity.LiveTailWeakMatch);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Weak_history_only_matches_do_not_prevent_one_confirmed_switch(int historyMatchCount)
    {
        var history = Enumerable.Range(0, historyMatchCount)
            .Select(index => Bubble(
                index % 2 == 0 ? 12 : 120,
                45 + (index * 25),
                (byte)(50 + (index * 20)),
                index % 2 == 0 ? MessageSide.Remote : MessageSide.Self))
            .ToArray();
        var previousVisible = new[]
        {
            Bubble(12, 55, 150, MessageSide.Remote),
            Bubble(120, 100, 170, MessageSide.Self),
        };
        var chatB = history
            .Select((bubble, index) => Bubble(
                bubble.Bounds.X,
                bubble.Bounds.Y,
                (byte)(56 + (index * 20)),
                bubble.Side))
            .ToArray();
        var detector = new StubBubbleDetector(
            history,
            previousVisible,
            chatB,
            chatB,
            chatB,
            chatB);
        var ocrResults = Enumerable.Range(1, historyMatchCount)
            .Select(index => Ocr($"old-history-{index}"))
            .Concat([Ocr("previous-1"), Ocr("previous-2")])
            .Concat(Enumerable.Range(1, historyMatchCount).Select(index => Ocr($"chat-b-{index}")))
            .ToArray();
        var ocr = new StubOcrEngine(ocrResults);
        var identity = new StubConversationIdentityProvider(
            Identity(1), Identity(1),
            Identity(2), Identity(2), Identity(2), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);
        var historyPixels = history
            .Select((bubble, index) => (bubble.Bounds, (byte)(50 + (index * 20))))
            .ToArray();
        var chatBPixels = chatB
            .Select((bubble, index) => (bubble.Bounds, (byte)(56 + (index * 20))))
            .ToArray();

        await observer.ObserveAsync(
            Frame(10, historyPixels),
            CancellationToken.None);
        await observer.ObserveAsync(
            Frame(10, [(previousVisible[0].Bounds, (byte)150), (previousVisible[1].Bounds, (byte)170)]),
            CancellationToken.None);
        var pendingOne = await observer.ObserveAsync(
            Frame(30, chatBPixels),
            CancellationToken.None);
        var pendingTwo = await observer.ObserveAsync(
            Frame(30, chatBPixels),
            CancellationToken.None);
        var switched = await observer.ObserveAsync(
            Frame(30, chatBPixels),
            CancellationToken.None);
        var stable = await observer.ObserveAsync(
            Frame(30, chatBPixels),
            CancellationToken.None);

        Assert.Equal(ConversationIdentityDecision.PendingSwitch, pendingOne.Identity.Decision);
        Assert.Equal(ConversationIdentityDecision.PendingSwitch, pendingTwo.Identity.Decision);
        Assert.Equal(0, pendingOne.Identity.PreviousVisibleStrongOverlap);
        Assert.Equal(0, pendingOne.Identity.PreviousVisibleWeakOverlap);
        Assert.Equal(historyMatchCount, pendingOne.Identity.HistoryOnlyMatches);
        Assert.Equal(ConversationIdentityDecision.ConfirmedSwitch, switched.Identity.Decision);
        Assert.Equal(2, switched.Epoch.Id);
        Assert.Equal(1, switched.Counters.ConversationSwitches);
        Assert.Equal(1, switched.Counters.IdentitySwitchesConfirmed);
        Assert.All(switched.MessagesObserved, message => Assert.Equal(MessageObservationKind.Bootstrap, message.Origin));
        Assert.Empty(switched.NewMessages);
        Assert.Equal(switched.Epoch.Id, stable.Epoch.Id);
        Assert.Equal(1, stable.Counters.ConversationSwitches);
        Assert.Equal((historyMatchCount * 2) + 2, ocr.Calls);
    }

    [Fact]
    public async Task Changed_header_with_trusted_live_tail_match_keeps_the_epoch()
    {
        var bubble = Bubble(12, 60, 80, MessageSide.Remote);
        var detector = new StubBubbleDetector([bubble], [bubble]);
        var ocr = new StubOcrEngine(Ocr("live-tail"), Ocr("live-tail"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        var baseline = await observer.ObserveAsync(
            Frame(10, [(bubble.Bounds, (byte)80)]),
            CancellationToken.None);
        var rerendered = await observer.ObserveAsync(
            Frame(30, [(bubble.Bounds, (byte)80)]),
            CancellationToken.None);

        Assert.Equal(baseline.Epoch.Id, rerendered.Epoch.Id);
        Assert.Equal(ConversationIdentityDecision.RebaseSameConversation, rerendered.Identity.Decision);
        Assert.True(rerendered.Identity.LiveTailStrongMatch);
        Assert.Equal(1, rerendered.Identity.PreviousVisibleStrongOverlap);
        Assert.Empty(rerendered.NewMessages);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Weak_visual_match_to_old_live_tail_is_not_strong_continuity()
    {
        var tail = Bubble(12, 60, 80, MessageSide.Remote);
        var detector = new StubBubbleDetector([tail], [tail]);
        var ocr = new StubOcrEngine(Ocr("chat-a-tail"), Ocr("chat-b-message"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        await observer.ObserveAsync(
            Frame(10, [(tail.Bounds, (byte)80)]),
            CancellationToken.None);
        var candidate = await observer.ObserveAsync(
            Frame(30, [(tail.Bounds, (byte)86)]),
            CancellationToken.None);

        Assert.Equal(ConversationIdentityDecision.PendingSwitch, candidate.Identity.Decision);
        Assert.Equal(0, candidate.Identity.PreviousVisibleStrongOverlap);
        Assert.Equal(1, candidate.Identity.PreviousVisibleWeakOverlap);
        Assert.False(candidate.Identity.LiveTailStrongMatch);
        Assert.True(candidate.Identity.LiveTailWeakMatch);
        Assert.Equal(1, candidate.Identity.PendingObservations);
        Assert.Equal(1, candidate.Epoch.Id);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Single_strict_visual_live_tail_match_without_trusted_text_is_not_strong_continuity()
    {
        var tail = Bubble(12, 60, 80, MessageSide.Remote);
        var detector = new StubBubbleDetector([tail], [tail]);
        var ocr = new StubOcrEngine(Ocr("chat-a-tail"), Ocr("different-chat-message"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        await observer.ObserveAsync(
            Frame(10, [(tail.Bounds, (byte)80)]),
            CancellationToken.None);
        var candidate = await observer.ObserveAsync(
            Frame(30, [(tail.Bounds, (byte)80)]),
            CancellationToken.None);

        Assert.Equal(ConversationIdentityDecision.PendingSwitch, candidate.Identity.Decision);
        Assert.Equal(1, candidate.Identity.PreviousVisibleStrongOverlap);
        Assert.Equal(0, candidate.Identity.TrustedTextOverlap);
        Assert.False(candidate.Identity.LiveTailStrongMatch);
        Assert.True(candidate.Identity.LiveTailWeakMatch);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Switching_back_confirms_one_epoch_and_bootstraps_without_replay()
    {
        var chatA = Bubble(12, 60, 80, MessageSide.Remote);
        var chatB = Bubble(120, 60, 120, MessageSide.Self);
        var detector = new StubBubbleDetector(
            [chatA], [chatB], [chatB], [chatB], [chatA], [chatA], [chatA]);
        var ocr = new StubOcrEngine(Ocr("chat-a"), Ocr("chat-b"), Ocr("chat-a"));
        var identity = new StubConversationIdentityProvider(
            Identity(1),
            Identity(2), Identity(2), Identity(2),
            Identity(1), Identity(1), Identity(1));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);
        var epochs = new List<long>();
        observer.ConversationChanged += (_, args) => epochs.Add(args.CurrentEpoch.Id);

        await observer.ObserveAsync(Frame(10, [(chatA.Bounds, (byte)80)]), CancellationToken.None);
        var toBPendingOne = await observer.ObserveAsync(
            Frame(30, [(chatB.Bounds, (byte)120)]),
            CancellationToken.None);
        var toBPendingTwo = await observer.ObserveAsync(
            Frame(30, [(chatB.Bounds, (byte)120)]),
            CancellationToken.None);
        var switchedToB = await observer.ObserveAsync(
            Frame(30, [(chatB.Bounds, (byte)120)]),
            CancellationToken.None);
        var toAPendingOne = await observer.ObserveAsync(
            Frame(10, [(chatA.Bounds, (byte)80)]),
            CancellationToken.None);
        var toAPendingTwo = await observer.ObserveAsync(
            Frame(10, [(chatA.Bounds, (byte)80)]),
            CancellationToken.None);
        var switchedBack = await observer.ObserveAsync(
            Frame(10, [(chatA.Bounds, (byte)80)]),
            CancellationToken.None);

        Assert.Equal(ConversationIdentityDecision.PendingSwitch, toBPendingOne.Identity.Decision);
        Assert.Equal(ConversationIdentityDecision.PendingSwitch, toBPendingTwo.Identity.Decision);
        Assert.Equal(ConversationIdentityDecision.ConfirmedSwitch, switchedToB.Identity.Decision);
        Assert.Equal(2, switchedToB.Epoch.Id);
        Assert.Equal(ConversationIdentityDecision.PendingSwitch, toAPendingOne.Identity.Decision);
        Assert.Equal(ConversationIdentityDecision.PendingSwitch, toAPendingTwo.Identity.Decision);
        Assert.Equal(3, switchedBack.Epoch.Id);
        Assert.Equal(ConversationIdentityDecision.ConfirmedSwitch, switchedBack.Identity.Decision);
        var bootstrap = Assert.Single(switchedBack.MessagesObserved);
        Assert.Equal(MessageObservationKind.Bootstrap, bootstrap.Origin);
        Assert.Equal("chat-a", bootstrap.NormalizedText);
        Assert.Empty(switchedBack.NewMessages);
        Assert.Equal([1L, 2L, 3L], epochs);
        Assert.Equal(2, switchedBack.Counters.ConversationSwitches);
        Assert.Equal(3, ocr.Calls);
    }

    [Fact]
    public async Task Empty_conversation_resize_rebases_after_layout_stabilizes_without_epoch_churn()
    {
        var detector = new StubBubbleDetector([], [], [], []);
        var ocr = new StubOcrEngine();
        var identity = new StubConversationIdentityProvider(
            Identity(1), Identity(2), Identity(2), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        var baseline = await observer.ObserveAsync(Frame(200, 200, 10, []), CancellationToken.None);
        var resize = await observer.ObserveAsync(Frame(300, 300, 30, []), CancellationToken.None);
        var settling = await observer.ObserveAsync(Frame(300, 300, 30, []), CancellationToken.None);
        var settled = await observer.ObserveAsync(Frame(300, 300, 30, []), CancellationToken.None);

        Assert.Equal(ConversationIdentityDecision.LayoutTransition, resize.Identity.Decision);
        Assert.Equal(ConversationIdentityDecision.LayoutTransition, settling.Identity.Decision);
        Assert.Equal(ConversationIdentityDecision.RebaseSameConversation, settled.Identity.Decision);
        Assert.Equal(baseline.Epoch.Id, settled.Epoch.Id);
        Assert.Equal(0, settled.Counters.ConversationSwitches);
        Assert.Equal(1, settled.Counters.LayoutTransitions);
        Assert.Equal(1, settled.Counters.IdentityRebases);
        Assert.Empty(observer.State.Messages);
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task Near_empty_resized_view_with_one_history_match_does_not_churn_the_epoch()
    {
        var first = Bubble(12, 60, 80, MessageSide.Remote);
        var tail = Bubble(12, 100, 120, MessageSide.Remote);
        var resizedFirst = new DetectedBubble(new CapturePixelRect(18, 90, 60, 36), MessageSide.Remote, 0.95);
        var detector = new StubBubbleDetector(
            [first, tail], [resizedFirst], [resizedFirst], [resizedFirst]);
        var ocr = new StubOcrEngine(Ocr("history"), Ocr("tail"), Ocr("history"));
        var identity = new StubConversationIdentityProvider(
            Identity(1), Identity(2), Identity(2), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        var baseline = await observer.ObserveAsync(
            Frame(200, 200, 10, [(first.Bounds, (byte)80), (tail.Bounds, (byte)120)]),
            CancellationToken.None);
        await observer.ObserveAsync(
            Frame(300, 300, 30, [(resizedFirst.Bounds, (byte)80)]),
            CancellationToken.None);
        await observer.ObserveAsync(
            Frame(300, 300, 30, [(resizedFirst.Bounds, (byte)80)]),
            CancellationToken.None);
        var settled = await observer.ObserveAsync(
            Frame(300, 300, 30, [(resizedFirst.Bounds, (byte)80)]),
            CancellationToken.None);

        Assert.Equal(baseline.Epoch.Id, settled.Epoch.Id);
        Assert.Equal(ConversationIdentityDecision.RebaseSameConversation, settled.Identity.Decision);
        Assert.Equal(1, settled.Identity.PreviousVisibleStrongOverlap);
        Assert.False(settled.Identity.LiveTailStrongMatch);
        Assert.Equal(0, settled.Counters.ConversationSwitches);
        Assert.Equal(3, ocr.Calls);
    }

    [Fact]
    public async Task Unstable_layout_with_one_visual_live_tail_reports_weak_tail_evidence()
    {
        var baselineTail = new DetectedBubble(
            new CapturePixelRect(12, 60, 40, 24),
            MessageSide.Remote,
            0.95);
        var resizedTail = new DetectedBubble(
            new CapturePixelRect(18, 90, 60, 36),
            MessageSide.Remote,
            0.95);
        var detector = new StubBubbleDetector([baselineTail], [resizedTail]);
        var ocr = new StubOcrEngine(Ocr("tail"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        await observer.ObserveAsync(
            Frame(200, 200, 10, [(baselineTail.Bounds, (byte)80)]),
            CancellationToken.None);
        var resizing = await observer.ObserveAsync(
            Frame(300, 300, 30, [(resizedTail.Bounds, (byte)80)]),
            CancellationToken.None);

        Assert.Equal(ConversationIdentityDecision.LayoutTransition, resizing.Identity.Decision);
        Assert.False(resizing.Identity.LiveTailStrongMatch);
        Assert.True(resizing.Identity.LiveTailWeakMatch);
        Assert.Equal(1, resizing.Epoch.Id);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task Same_size_chat_roi_change_starts_a_layout_transition()
    {
        var firstRegion = new CapturePixelRect(0, 20, 200, 180);
        var shiftedRegion = new CapturePixelRect(20, 20, 180, 180);
        var bubble = Bubble(40, 60, 80, MessageSide.Remote);
        var detector = new StubBubbleDetector([bubble], [bubble]);
        var ocr = new StubOcrEngine(Ocr("same-message"), Ocr("same-message"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(1));
        var observer = CreateObserver(
            detector,
            ocr,
            identityProvider: identity,
            chatRegionLocator: new SequencedChatRegionLocator(firstRegion, shiftedRegion));

        await observer.ObserveAsync(
            Frame(10, [(bubble.Bounds, (byte)80)]),
            CancellationToken.None);
        var shifted = await observer.ObserveAsync(
            Frame(10, [(bubble.Bounds, (byte)120)]),
            CancellationToken.None);

        Assert.Equal(1, shifted.Epoch.Id);
        Assert.Equal(1, shifted.Counters.LayoutTransitions);
    }

    [Fact]
    public async Task Returning_to_the_accepted_identity_clears_an_interrupted_pending_switch()
    {
        var detector = new StubBubbleDetector([], [], []);
        var ocr = new StubOcrEngine();
        var identity = new StubConversationIdentityProvider(
            Identity(1), Identity(2), Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);
        var empty = Frame(10, []);

        await observer.ObserveAsync(empty, CancellationToken.None);
        var firstCandidate = await observer.ObserveAsync(empty, CancellationToken.None);
        var returned = await observer.ObserveAsync(empty, CancellationToken.None);
        var candidateAfterInterruption = await observer.ObserveAsync(empty, CancellationToken.None);

        Assert.Equal(ConversationIdentityDecision.PendingSwitch, firstCandidate.Identity.Decision);
        Assert.Equal(1, firstCandidate.Identity.PendingObservations);
        Assert.Equal(ConversationIdentityDecision.Same, returned.Identity.Decision);
        Assert.Equal(ConversationIdentityDecision.PendingSwitch, candidateAfterInterruption.Identity.Decision);
        Assert.Equal(1, candidateAfterInterruption.Identity.PendingObservations);
        Assert.Equal(1, candidateAfterInterruption.Epoch.Id);
        Assert.Equal(0, candidateAfterInterruption.Counters.ConversationSwitches);
    }

    [Fact]
    public async Task Replacing_a_pending_candidate_does_not_reuse_the_previous_candidates_ocr()
    {
        var chatA = Bubble(12, 60, 80, MessageSide.Remote);
        var candidateBubble = Bubble(120, 60, 120, MessageSide.Self);
        var detector = new StubBubbleDetector(
            [chatA], [candidateBubble], [candidateBubble], [candidateBubble], [candidateBubble]);
        var ocr = new StubOcrEngine(Ocr("chat-a"), Ocr("chat-b"), Ocr("chat-c"));
        var identity = new StubConversationIdentityProvider(
            Identity(1), Identity(2), Identity(3), Identity(3), Identity(3));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        await observer.ObserveAsync(Frame(10, [(chatA.Bounds, (byte)80)]), CancellationToken.None);
        var candidateB = await observer.ObserveAsync(
            Frame(20, [(candidateBubble.Bounds, (byte)120)]),
            CancellationToken.None);
        var candidateC = await observer.ObserveAsync(
            Frame(30, [(candidateBubble.Bounds, (byte)120)]),
            CancellationToken.None);
        await observer.ObserveAsync(
            Frame(30, [(candidateBubble.Bounds, (byte)120)]),
            CancellationToken.None);
        var switched = await observer.ObserveAsync(
            Frame(30, [(candidateBubble.Bounds, (byte)120)]),
            CancellationToken.None);

        Assert.Equal(1, candidateB.Identity.PendingObservations);
        Assert.Equal(1, candidateC.Identity.PendingObservations);
        Assert.Equal(ConversationIdentityDecision.ConfirmedSwitch, switched.Identity.Decision);
        Assert.Equal("chat-c", Assert.Single(switched.MessagesObserved).NormalizedText);
        Assert.Equal(3, ocr.Calls);
    }

    [Fact]
    public async Task Resizing_the_same_conversation_wider_and_narrower_keeps_the_epoch()
    {
        var baselineBubble = new DetectedBubble(new CapturePixelRect(18, 90, 60, 36), MessageSide.Remote, 0.95);
        var narrowBubble = new DetectedBubble(new CapturePixelRect(12, 60, 40, 24), MessageSide.Remote, 0.95);
        var wideBubble = new DetectedBubble(new CapturePixelRect(24, 120, 80, 48), MessageSide.Remote, 0.95);
        var detector = new StubBubbleDetector([baselineBubble], [narrowBubble], [wideBubble]);
        var ocr = new StubOcrEngine(Ocr("same-message"));
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(1), Identity(1));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        var baseline = await observer.ObserveAsync(
            Frame(300, 300, 10, [(baselineBubble.Bounds, (byte)80)]),
            CancellationToken.None);
        var narrowed = await observer.ObserveAsync(
            Frame(200, 200, 10, [(narrowBubble.Bounds, (byte)80)]),
            CancellationToken.None);
        var widened = await observer.ObserveAsync(
            Frame(400, 400, 10, [(wideBubble.Bounds, (byte)80)]),
            CancellationToken.None);

        Assert.Equal(baseline.Epoch.Id, narrowed.Epoch.Id);
        Assert.Equal(baseline.Epoch.Id, widened.Epoch.Id);
        Assert.Empty(narrowed.MessagesObserved);
        Assert.Empty(widened.MessagesObserved);
        Assert.Equal(0, widened.Counters.ConversationSwitches);
        Assert.Equal(2, widened.Counters.LayoutTransitions);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task Resize_with_six_previous_visible_messages_rebases_the_same_epoch()
    {
        var baselineBubbles = Enumerable.Range(0, 6)
            .Select(index => new DetectedBubble(
                new CapturePixelRect(index % 2 == 0 ? 12 : 120, 45 + (index * 22), 40, 18),
                index % 2 == 0 ? MessageSide.Remote : MessageSide.Self,
                0.95))
            .ToArray();
        var resizedBubbles = baselineBubbles
            .Select(bubble => new DetectedBubble(
                new CapturePixelRect(
                    bubble.Bounds.X * 3 / 2,
                    bubble.Bounds.Y * 3 / 2,
                    bubble.Bounds.Width * 3 / 2,
                    bubble.Bounds.Height * 3 / 2),
                bubble.Side,
                bubble.DetectionScore))
            .ToArray();
        var pixelValues = new byte[] { 50, 70, 90, 110, 130, 150 };
        var detector = new StubBubbleDetector(baselineBubbles, resizedBubbles);
        var ocr = new StubOcrEngine(pixelValues.Select((_, index) => Ocr($"message-{index + 1}")).ToArray());
        var identity = new StubConversationIdentityProvider(Identity(1), Identity(2));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        var baseline = await observer.ObserveAsync(
            Frame(200, 200, 10, baselineBubbles.Select((bubble, index) => (bubble.Bounds, pixelValues[index])).ToArray()),
            CancellationToken.None);
        var resized = await observer.ObserveAsync(
            Frame(300, 300, 30, resizedBubbles.Select((bubble, index) => (bubble.Bounds, pixelValues[index])).ToArray()),
            CancellationToken.None);

        Assert.Equal(baseline.Epoch.Id, resized.Epoch.Id);
        Assert.Equal(ConversationIdentityDecision.RebaseSameConversation, resized.Identity.Decision);
        Assert.Equal(6, resized.Identity.PreviousVisibleStrongOverlap);
        Assert.Equal(0, resized.Identity.PreviousVisibleWeakOverlap);
        Assert.Equal(0, resized.Identity.TrustedTextOverlap);
        Assert.True(resized.Identity.LiveTailStrongMatch);
        Assert.False(resized.Identity.LiveTailWeakMatch);
        Assert.Empty(resized.NewMessages);
        Assert.Equal(6, ocr.Calls);
        Assert.Equal(0, resized.Counters.ConversationSwitches);
    }

    [Fact]
    public async Task Simulated_dpi_rerender_keeps_the_epoch_with_real_visual_identity_evidence()
    {
        var laptopBubble = new DetectedBubble(new CapturePixelRect(18, 90, 60, 36), MessageSide.Remote, 0.95);
        var externalBubble = new DetectedBubble(new CapturePixelRect(12, 60, 40, 24), MessageSide.Remote, 0.95);
        var detector = new StubBubbleDetector([laptopBubble], [externalBubble]);
        var ocr = new StubOcrEngine(Ocr("same-message"));
        var observer = CreateObserver(detector, ocr);

        var laptop = await observer.ObserveAsync(
            PatternedFrame(300, 300, laptopBubble.Bounds, 80),
            CancellationToken.None);
        var external = await observer.ObserveAsync(
            PatternedFrame(200, 200, externalBubble.Bounds, 80),
            CancellationToken.None);

        Assert.Equal(laptop.Epoch.Id, external.Epoch.Id);
        Assert.Empty(external.MessagesObserved);
        Assert.Empty(external.NewMessages);
        Assert.Equal(0, external.Counters.ConversationSwitches);
        Assert.Equal(1, external.Counters.LayoutTransitions);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task Gradual_resize_with_different_header_renderings_has_zero_epoch_churn()
    {
        var sizes = new[] { 200, 230, 260, 290, 320 };
        var bubbles = sizes
            .Select(size => new DetectedBubble(
                new CapturePixelRect(size / 16, size * 3 / 10, size / 5, size * 3 / 25),
                MessageSide.Remote,
                0.95))
            .ToArray();
        var detector = new StubBubbleDetector(bubbles.Select(bubble =>
            (IReadOnlyList<DetectedBubble>)[bubble]).ToArray());
        var ocr = new StubOcrEngine(Ocr("same-message"));
        var identity = new StubConversationIdentityProvider(
            Identity(1), Identity(2), Identity(3), Identity(4), Identity(5));
        var observer = CreateObserver(detector, ocr, identityProvider: identity);

        ObservationResult? result = null;
        for (var index = 0; index < sizes.Length; index++)
        {
            result = await observer.ObserveAsync(
                Frame(sizes[index], sizes[index], 10, [(bubbles[index].Bounds, (byte)80)]),
                CancellationToken.None);
        }

        Assert.NotNull(result);
        Assert.Equal(1, result.Epoch.Id);
        Assert.Equal(0, result.Counters.ConversationSwitches);
        Assert.Equal(4, result.Counters.LayoutTransitions);
        Assert.Equal(0, result.Counters.IdentityRebases);
        Assert.Equal(1, ocr.Calls);
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
        ObserverOptions? options = null,
        IConversationIdentityProvider? identityProvider = null,
        IChatRegionLocator? chatRegionLocator = null) =>
        new MessageObserver(
            chatRegionLocator ?? new StubChatRegionLocator(),
            detector,
            ocr,
            new ChatRoiChangeDetector(),
            identityProvider ?? new VisualConversationIdentityProvider(),
            options);

    private static IConversationIdentityEvidence Identity(ulong value) =>
        new StubConversationIdentityEvidence(value);

    private static DetectedBubble Bubble(int x, int y, byte value, MessageSide side) =>
        new(new CapturePixelRect(x, y, 40, 24), side, value / 255d);

    private static OcrResult Ocr(string text) =>
        new(text, 0.98, OcrTextStatus.Recognized, text);

    private static CapturedFrame Frame(byte headerValue, byte bubbleValue) =>
        Frame(headerValue, [(new CapturePixelRect(12, 60, 50, 24), bubbleValue)]);

    private static CapturedFrame Frame(
        byte headerValue,
        IReadOnlyList<(CapturePixelRect Rect, byte Value)> bubbles) =>
        Frame(200, 200, headerValue, bubbles);

    private static CapturedFrame Frame(
        int width,
        int height,
        byte headerValue,
        IReadOnlyList<(CapturePixelRect Rect, byte Value)> bubbles)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Fill(pixels, stride, new CapturePixelRect(0, 0, width, Math.Max(1, height / 10)), headerValue);
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

    private static CapturedFrame PatternedFrame(
        int width,
        int height,
        CapturePixelRect bubble,
        byte bubbleValue)
    {
        var frame = Frame(width, height, 20, [(bubble, bubbleValue)]);
        var pixels = frame.Bgra32Pixels.ToArray();
        var headerHeight = Math.Max(1, height / 10);
        Fill(
            pixels,
            frame.Stride,
            new CapturePixelRect(width / 10, headerHeight / 3, width / 5, Math.Max(1, headerHeight / 3)),
            180);
        return CloneWithPixels(frame, pixels);
    }

    private static CapturedFrame CloneWithFill(
        CapturedFrame frame,
        CapturePixelRect rect,
        byte value)
    {
        var pixels = frame.Bgra32Pixels.ToArray();
        Fill(pixels, frame.Stride, rect, value);
        return CloneWithPixels(frame, pixels);
    }

    private static CapturedFrame CloneWithPixels(CapturedFrame frame, byte[] pixels) =>
        new(
            frame.Width,
            frame.Height,
            frame.Stride,
            pixels,
            frame.DesktopBounds,
            frame.Method,
            frame.CapturedAt,
            frame.Duration);

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
        public DetectedChatRegion Locate(CapturedFrame frame)
        {
            var headerHeight = Math.Max(1, frame.Height / 10);
            return new(
                new CapturePixelRect(0, headerHeight, frame.Width, frame.Height - headerHeight),
                1);
        }
    }

    private sealed class SequencedChatRegionLocator(params CapturePixelRect[] regions) : IChatRegionLocator
    {
        private int _calls;

        public DetectedChatRegion Locate(CapturedFrame frame)
        {
            var index = Math.Min(_calls, regions.Length - 1);
            _calls++;
            return new(regions[index], 1);
        }
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

    private sealed record StubConversationIdentityEvidence(ulong Value) : IConversationIdentityEvidence;

    private sealed class StubConversationIdentityProvider(params IConversationIdentityEvidence[] identities)
        : IConversationIdentityProvider
    {
        private int _calls;

        public IConversationIdentityEvidence GetVisualEvidence(
            CapturedFrame frame,
            CapturePixelRect chatRegion)
        {
            var index = Math.Min(_calls, identities.Length - 1);
            _calls++;
            return identities[index];
        }

        public ConversationIdentityComparison Compare(
            IConversationIdentityEvidence accepted,
            IConversationIdentityEvidence candidate) =>
            Equals(accepted, candidate)
                ? new(true, "stub_identity_equal=true")
                : new(false, "stub_identity_equal=false");
    }
}
