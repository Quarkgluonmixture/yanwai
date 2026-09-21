using System.Diagnostics;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Ocr;
using WeChatJevHud.Vision;

namespace WeChatJevHud.Observer;

public sealed class MessageObserver : IMessageObserver
{
    private readonly IChatRegionLocator _chatRegionLocator;
    private readonly IBubbleDetector _bubbleDetector;
    private readonly IOcrEngine _ocrEngine;
    private readonly IChatRoiChangeDetector _changeDetector;
    private readonly IConversationIdentityProvider _conversationIdentityProvider;
    private readonly ObserverOptions _options;
    private readonly List<ObservedMessage> _messages = [];
    private readonly List<VisibleMessageSnapshot> _visibleMessages = [];
    private ConversationEpoch? _epoch;
    private CapturePixelRect? _chatRegion;
    private string? _lastFrameFingerprint;
    private int _frameWidth;
    private int _frameHeight;
    private long _nextMessageId;
    private long _framesChecked;
    private long _unchangedFrames;
    private long _changedFrames;
    private long _bubbleDetectionRuns;
    private long _ocrCalls;
    private long _messagesEmitted;
    private long _duplicatesSuppressed;
    private long _conversationSwitches;
    private long _identityMismatchCandidates;
    private long _identityRebases;
    private long _identitySwitchesConfirmed;
    private long _identitySwitchesSuppressed;
    private long _layoutTransitions;
    private bool _baselineEstablished;
    private string? _liveTailId;
    private PendingConversationSwitch? _pendingSwitch;
    private bool _layoutTransitionActive;
    private int _layoutStableObservationCount;

    public MessageObserver(
        IChatRegionLocator chatRegionLocator,
        IBubbleDetector bubbleDetector,
        IOcrEngine ocrEngine,
        IChatRoiChangeDetector changeDetector,
        IConversationIdentityProvider conversationIdentityProvider,
        ObserverOptions? options = null)
    {
        _chatRegionLocator = chatRegionLocator ?? throw new ArgumentNullException(nameof(chatRegionLocator));
        _bubbleDetector = bubbleDetector ?? throw new ArgumentNullException(nameof(bubbleDetector));
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _changeDetector = changeDetector ?? throw new ArgumentNullException(nameof(changeDetector));
        _conversationIdentityProvider = conversationIdentityProvider ?? throw new ArgumentNullException(nameof(conversationIdentityProvider));
        _options = options ?? new ObserverOptions();
        if (_options.RecentMessageLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Recent message limit must be positive.");
        }

        if (_options.PendingSwitchRequiredObservations < 2 ||
            _options.LayoutStableObservations < 1 ||
            _options.BubbleMaxHammingDistance is < 0 or > 128 ||
            _options.BubbleMaxMeanLuminanceDifference is < 0 or > 255 ||
            _options.BubbleStrongMaxHammingDistance is < 0 or > 128 ||
            _options.BubbleStrongMaxMeanLuminanceDifference is < 0 or > 255 ||
            _options.BubbleStrongMaxHammingDistance > _options.BubbleMaxHammingDistance ||
            _options.BubbleStrongMaxMeanLuminanceDifference > _options.BubbleMaxMeanLuminanceDifference)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Conversation identity options are outside their valid ranges.");
        }
    }

    public event EventHandler<ConversationChangedEventArgs>? ConversationChanged;

    public event EventHandler<MessageObservedEventArgs>? MessageObserved;

    public event EventHandler<MessageObservedEventArgs>? NewMessageObserved;

    public RecentConversationSnapshot State =>
        new(_epoch, _messages.ToArray(), _visibleMessages.ToArray());

    public ObserverCounters Counters => new(
        _framesChecked,
        _unchangedFrames,
        _changedFrames,
        _bubbleDetectionRuns,
        _ocrCalls,
        _messagesEmitted,
        _duplicatesSuppressed,
        _conversationSwitches,
        _identityMismatchCandidates,
        _identityRebases,
        _identitySwitchesConfirmed,
        _identitySwitchesSuppressed,
        _layoutTransitions);

    public async Task<ObservationResult> ObserveAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var frameTimer = Stopwatch.StartNew();
        _framesChecked++;

        var previousChatRegion = _chatRegion;
        var dimensionsChanged = _frameWidth != frame.Width || _frameHeight != frame.Height;
        if (_chatRegion is null || dimensionsChanged)
        {
            _chatRegion = _chatRegionLocator.Locate(frame).Bounds;
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
        }

        var layoutChanged = previousChatRegion is not null &&
                            (dimensionsChanged || !previousChatRegion.Value.Equals(_chatRegion!.Value));
        var changeTimer = Stopwatch.StartNew();
        var identity = _conversationIdentityProvider.GetVisualEvidence(frame, _chatRegion!.Value);
        var tentativeIdentityMismatch = _epoch is not null &&
                                        !IdentityMatches(
                                            _conversationIdentityProvider.Compare(_epoch.VisualIdentity, identity));
        var frameFingerprint = _changeDetector.ComputeFingerprint(frame, _chatRegion.Value);
        var frameChanged = _epoch is null || tentativeIdentityMismatch ||
                           !string.Equals(_lastFrameFingerprint, frameFingerprint, StringComparison.Ordinal);
        if (!dimensionsChanged && previousChatRegion is not null && frameChanged)
        {
            var refreshedChatRegion = _chatRegionLocator.Locate(frame).Bounds;
            if (!_chatRegion.Value.Equals(refreshedChatRegion))
            {
                _chatRegion = refreshedChatRegion;
                layoutChanged = true;
                identity = _conversationIdentityProvider.GetVisualEvidence(frame, _chatRegion.Value);
                frameFingerprint = _changeDetector.ComputeFingerprint(frame, _chatRegion.Value);
            }
        }

        if (layoutChanged)
        {
            _layoutTransitions++;
            _layoutTransitionActive = true;
            _layoutStableObservationCount = 0;
            _pendingSwitch = null;
        }
        else if (_layoutTransitionActive)
        {
            _layoutStableObservationCount++;
        }

        var isInitialEpoch = _epoch is null;
        if (isInitialEpoch)
        {
            StartEpoch(identity, frame.CapturedAt);
        }

        var identityDistance = isInitialEpoch
            ? new ConversationIdentityComparison(true, "initial_identity=true")
            : _conversationIdentityProvider.Compare(_epoch!.VisualIdentity, identity);
        var identityMismatch = !isInitialEpoch && !IdentityMatches(identityDistance);
        if (identityMismatch)
        {
            _identityMismatchCandidates++;
        }

        var identityObservation = new ConversationIdentityObservation(
            isInitialEpoch ? ConversationIdentityDecision.Initial : ConversationIdentityDecision.Same,
            identityMismatch,
            identityDistance.Diagnostics,
            0,
            0,
            0,
            LiveTailStrongMatch: false,
            LiveTailWeakMatch: false,
            HistoryOnlyMatches: 0,
            VisibleCandidates: 0,
            PendingObservations: 0,
            _options.PendingSwitchRequiredObservations,
            layoutChanged);
        if (!identityMismatch && _pendingSwitch is not null)
        {
            _pendingSwitch = null;
            _identitySwitchesSuppressed++;
        }

        frameChanged = isInitialEpoch || identityMismatch ||
                       !string.Equals(_lastFrameFingerprint, frameFingerprint, StringComparison.Ordinal);
        changeTimer.Stop();
        frameTimer.Stop();
        var frameCheckDuration = frameTimer.Elapsed;

        if (!frameChanged)
        {
            _unchangedFrames++;
            return Result(
                frameChanged: false,
                [],
                [],
                [],
                new ObserverTimings(frameCheckDuration, changeTimer.Elapsed, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
                identityObservation);
        }

        _changedFrames++;
        var bubbleTimer = Stopwatch.StartNew();
        var bubbles = _bubbleDetector.Detect(frame, _chatRegion.Value)
            .OrderBy(bubble => bubble.Bounds.Y)
            .ThenBy(bubble => bubble.Bounds.X)
            .ToArray();
        bubbleTimer.Stop();
        _bubbleDetectionRuns++;

        var reconcileTimer = Stopwatch.StartNew();
        var candidates = bubbles
            .Select(bubble => new VisibleCandidate(
                bubble,
                PixelFingerprint.ComputePerceptual(frame, bubble.Bounds).Signature))
            .ToArray();
        var previousVisibleMessages = PreviousVisibleMessages();
        var weakHistoryMatches = _baselineEstablished && !identityMismatch
            ? SequenceAlignment.Align(
                _messages.Count,
                candidates.Length,
                (message, candidate) =>
                    CandidateVisuallyMatches(_messages[message], candidates[candidate]))
            : [];
        var strongVisualPreviousMatches = _baselineEstablished
            ? SequenceAlignment.Align(
                previousVisibleMessages.Count,
                candidates.Length,
                (message, candidate) =>
                    CandidateStronglyVisuallyMatches(previousVisibleMessages[message], candidates[candidate]))
            : [];
        var weakVisualPreviousMatches = _baselineEstablished
            ? SequenceAlignment.Align(
                previousVisibleMessages.Count,
                candidates.Length,
                (message, candidate) =>
                    CandidateVisuallyMatches(previousVisibleMessages[message], candidates[candidate]))
            : [];
        foreach (var (messageIndex, candidateIndex) in weakHistoryMatches)
        {
            candidates[candidateIndex].Ocr = OcrFrom(_messages[messageIndex]);
        }

        foreach (var (messageIndex, candidateIndex) in strongVisualPreviousMatches)
        {
            candidates[candidateIndex].Ocr = OcrFrom(previousVisibleMessages[messageIndex]);
        }

        HydrateFromPendingSwitch(candidates, identity);

        var strongVisualLiveTailMatched = strongVisualPreviousMatches.Any(match =>
            string.Equals(previousVisibleMessages[match.Left].Id, _liveTailId, StringComparison.Ordinal));
        var weakVisualLiveTailMatched = weakVisualPreviousMatches.Any(match =>
            string.Equals(previousVisibleMessages[match.Left].Id, _liveTailId, StringComparison.Ordinal));
        var hasStrongVisualContinuity =
            strongVisualPreviousMatches.Count >= 2 ||
            (previousVisibleMessages.Count > 0 &&
             strongVisualPreviousMatches.Count == previousVisibleMessages.Count) ||
            (strongVisualLiveTailMatched && previousVisibleMessages.Count == 1);
        var layoutHasStabilized = !_layoutTransitionActive ||
                                  _layoutStableObservationCount >= _options.LayoutStableObservations;
        if (identityMismatch &&
            _layoutTransitionActive &&
            !layoutHasStabilized &&
            !hasStrongVisualContinuity)
        {
            _identitySwitchesSuppressed++;
            _lastFrameFingerprint = frameFingerprint;
            reconcileTimer.Stop();
            identityObservation = identityObservation with
            {
                Decision = ConversationIdentityDecision.LayoutTransition,
                PreviousVisibleStrongOverlap = strongVisualPreviousMatches.Count,
                PreviousVisibleWeakOverlap = weakVisualPreviousMatches.Count,
                VisibleCandidates = candidates.Length,
                LiveTailStrongMatch = false,
                LiveTailWeakMatch = weakVisualLiveTailMatched,
            };
            return Result(
                frameChanged: true,
                [],
                [],
                [],
                new ObserverTimings(
                    frameCheckDuration,
                    changeTimer.Elapsed,
                    bubbleTimer.Elapsed,
                    TimeSpan.Zero,
                    reconcileTimer.Elapsed),
                identityObservation);
        }

        reconcileTimer.Stop();
        var ocrDuration = TimeSpan.Zero;
        foreach (var candidate in candidates.Where(candidate => candidate.Ocr is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ocrTimer = Stopwatch.StartNew();
            candidate.Ocr = await _ocrEngine.RecognizeAsync(
                new ImageCrop(frame, candidate.Bubble.Bounds),
                cancellationToken).ConfigureAwait(false);
            ocrTimer.Stop();
            ocrDuration += ocrTimer.Elapsed;
            _ocrCalls++;
        }

        reconcileTimer.Start();
        var matches = _baselineEstablished
            ? SequenceAlignment.Align(
                _messages.Count,
                candidates.Length,
                (message, candidate) => CandidateMatches(_messages[message], candidates[candidate]))
            : [];
        var trustedTextPreviousMatches = _baselineEstablished
            ? SequenceAlignment.Align(
                previousVisibleMessages.Count,
                candidates.Length,
                (message, candidate) =>
                    CandidateTrustedTextMatches(previousVisibleMessages[message], candidates[candidate]))
            : [];
        var strongPreviousMatches = _baselineEstablished
            ? SequenceAlignment.Align(
                previousVisibleMessages.Count,
                candidates.Length,
                (message, candidate) =>
                    CandidateStronglyMatches(previousVisibleMessages[message], candidates[candidate]))
            : [];
        var trustedTextLiveTailMatched = trustedTextPreviousMatches.Any(match =>
            string.Equals(previousVisibleMessages[match.Left].Id, _liveTailId, StringComparison.Ordinal));
        var strongVisualLiveTailHasOrderedContinuity = strongVisualLiveTailMatched &&
                                                       (strongVisualPreviousMatches.Count >= 2 ||
                                                        previousVisibleMessages.Count == 1);
        var liveTailStrongMatched = trustedTextLiveTailMatched || strongVisualLiveTailHasOrderedContinuity;
        var hasStrongPreviousVisibleContinuity =
            strongPreviousMatches.Count >= 2 ||
            (previousVisibleMessages.Count > 0 &&
             strongPreviousMatches.Count == previousVisibleMessages.Count) ||
            liveTailStrongMatched ||
            (_layoutTransitionActive && strongPreviousMatches.Count >= 1);
        var previousVisibleIds = previousVisibleMessages
            .Select(message => message.Id)
            .ToHashSet(StringComparer.Ordinal);
        var historyOnlyMatches = matches.Count(match =>
            !previousVisibleIds.Contains(_messages[match.Left].Id));
        if (identityMismatch)
        {
            if (hasStrongPreviousVisibleContinuity)
            {
                _epoch = _epoch! with { VisualIdentity = identity };
                _pendingSwitch = null;
                _layoutTransitionActive = false;
                _identityRebases++;
                identityObservation = identityObservation with
                {
                    Decision = ConversationIdentityDecision.RebaseSameConversation,
                    PreviousVisibleStrongOverlap = strongPreviousMatches.Count,
                    PreviousVisibleWeakOverlap = weakVisualPreviousMatches.Count,
                    TrustedTextOverlap = trustedTextPreviousMatches.Count,
                    LiveTailStrongMatch = liveTailStrongMatched,
                    LiveTailWeakMatch = weakVisualLiveTailMatched,
                    HistoryOnlyMatches = historyOnlyMatches,
                    VisibleCandidates = candidates.Length,
                };
            }
            else if (_layoutTransitionActive &&
                     layoutHasStabilized &&
                     _messages.Count == 0 &&
                     candidates.Length == 0)
            {
                _epoch = _epoch! with { VisualIdentity = identity };
                _pendingSwitch = null;
                _layoutTransitionActive = false;
                _identityRebases++;
                identityObservation = identityObservation with
                {
                    Decision = ConversationIdentityDecision.RebaseSameConversation,
                    VisibleCandidates = 0,
                };
            }
            else
            {
                var observations = _pendingSwitch is not null &&
                                   IdentityMatches(_pendingSwitch.Identity, identity)
                    ? _pendingSwitch.Observations + 1
                    : 1;
                _pendingSwitch = new PendingConversationSwitch(
                    identity,
                    observations,
                    candidates.Select(PendingCandidate.From).ToArray());
                _lastFrameFingerprint = frameFingerprint;
                if (observations < _options.PendingSwitchRequiredObservations)
                {
                    _identitySwitchesSuppressed++;
                    identityObservation = identityObservation with
                    {
                        Decision = ConversationIdentityDecision.PendingSwitch,
                        PreviousVisibleStrongOverlap = strongPreviousMatches.Count,
                        PreviousVisibleWeakOverlap = weakVisualPreviousMatches.Count,
                        TrustedTextOverlap = trustedTextPreviousMatches.Count,
                        LiveTailStrongMatch = liveTailStrongMatched,
                        LiveTailWeakMatch = weakVisualLiveTailMatched,
                        HistoryOnlyMatches = historyOnlyMatches,
                        VisibleCandidates = candidates.Length,
                        PendingObservations = observations,
                    };
                    reconcileTimer.Stop();
                    return Result(
                        frameChanged: true,
                        [],
                        [],
                        [],
                        new ObserverTimings(
                            frameCheckDuration,
                            changeTimer.Elapsed,
                            bubbleTimer.Elapsed,
                            ocrDuration,
                            reconcileTimer.Elapsed),
                        identityObservation);
                }

                StartEpoch(identity, frame.CapturedAt);
                _pendingSwitch = null;
                _layoutTransitionActive = false;
                _identitySwitchesConfirmed++;
                identityObservation = identityObservation with
                {
                    Decision = ConversationIdentityDecision.ConfirmedSwitch,
                    PreviousVisibleStrongOverlap = strongPreviousMatches.Count,
                    PreviousVisibleWeakOverlap = weakVisualPreviousMatches.Count,
                    TrustedTextOverlap = trustedTextPreviousMatches.Count,
                    LiveTailStrongMatch = liveTailStrongMatched,
                    LiveTailWeakMatch = weakVisualLiveTailMatched,
                    HistoryOnlyMatches = historyOnlyMatches,
                    VisibleCandidates = candidates.Length,
                    PendingObservations = observations,
                };
                matches = [];
            }
        }
        else
        {
            _pendingSwitch = null;
            if (_layoutTransitionActive && layoutHasStabilized)
            {
                _layoutTransitionActive = false;
            }
        }

        var matchedByCandidate = matches.ToDictionary(match => match.Right, match => match.Left);
        var liveTailCandidateIndex = matches
            .Where(match => string.Equals(_messages[match.Left].Id, _liveTailId, StringComparison.Ordinal))
            .Select(match => (int?)match.Right)
            .SingleOrDefault();
        var timelineWasEmpty = _messages.Count == 0;
        for (var index = 0; index < _messages.Count; index++)
        {
            _messages[index] = _messages[index] with { IsVisible = false };
        }

        var duplicateIds = new List<string>(matches.Count);
        foreach (var (messageIndex, candidateIndex) in matches)
        {
            var messageId = _messages[messageIndex].Id;
            var currentIndex = _messages.FindIndex(message => message.Id == messageId);
            var updated = _messages[currentIndex] with
            {
                BubbleRect = candidates[candidateIndex].Bubble.Bounds,
                VisualFingerprint = candidates[candidateIndex].VisualFingerprint,
                IsVisible = true,
            };
            _messages[currentIndex] = updated;
            candidates[candidateIndex].Message = updated;
            duplicateIds.Add(updated.Id);
        }

        var observed = new List<ObservedMessage>(candidates.Length - matches.Count);
        var emitted = new List<ObservedMessage>();
        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            if (matchedByCandidate.ContainsKey(candidateIndex))
            {
                continue;
            }

            var candidate = candidates[candidateIndex];
            var hasDistinctAnchor = matches.Any(match =>
                !CandidateMatches(_messages[match.Left], candidate));
            var origin = !_baselineEstablished
                ? MessageObservationKind.Bootstrap
                : timelineWasEmpty ||
                  (liveTailCandidateIndex is { } liveTail && candidateIndex > liveTail && hasDistinctAnchor)
                    ? MessageObservationKind.LiveNew
                    : MessageObservationKind.History;
            var ocr = candidate.Ocr!;
            var message = new ObservedMessage(
                $"e{_epoch!.Id:D4}-m{++_nextMessageId:D6}",
                _epoch.Id,
                candidate.Bubble.Side,
                ocr.Text,
                ocr.RawText,
                ocr.Status,
                ocr.OcrConfidence,
                candidate.Bubble.Bounds,
                frame.CapturedAt,
                origin,
                candidate.VisualFingerprint,
                IsVisible: true);
            observed.Add(message);
            candidate.Message = message;
            InsertInTimeline(candidates, candidateIndex, message);
            if (origin == MessageObservationKind.LiveNew)
            {
                emitted.Add(message);
                _liveTailId = message.Id;
            }
        }

        _baselineEstablished = true;
        if (_liveTailId is null && candidates.LastOrDefault()?.Message is { } baselineTail)
        {
            _liveTailId = baselineTail.Id;
        }

        _visibleMessages.Clear();
        _visibleMessages.AddRange(candidates.Select(candidate => new VisibleMessageSnapshot(
            candidate.Message!.Id,
            candidate.Message.Side,
            candidate.Bubble.Bounds,
            candidate.VisualFingerprint)));
        TrimRecentState();
        _lastFrameFingerprint = frameFingerprint;
        foreach (var message in observed)
        {
            MessageObserved?.Invoke(this, new MessageObservedEventArgs(message));
        }

        reconcileTimer.Stop();
        return Result(
            frameChanged: true,
            observed,
            emitted,
            duplicateIds,
            new ObserverTimings(frameCheckDuration, changeTimer.Elapsed, bubbleTimer.Elapsed, ocrDuration, reconcileTimer.Elapsed),
            identityObservation);
    }

    private void StartEpoch(IConversationIdentityEvidence identity, DateTimeOffset observedAt)
    {
        var previous = _epoch;
        if (previous is not null)
        {
            _conversationSwitches++;
        }

        _epoch = new ConversationEpoch((previous?.Id ?? 0) + 1, identity, observedAt);
        _messages.Clear();
        _visibleMessages.Clear();
        _lastFrameFingerprint = null;
        _baselineEstablished = false;
        _liveTailId = null;
        _pendingSwitch = null;
        ConversationChanged?.Invoke(this, new ConversationChangedEventArgs(previous, _epoch));
    }

    private bool IdentityMatches(
        IConversationIdentityEvidence accepted,
        IConversationIdentityEvidence candidate)
    {
        var distance = _conversationIdentityProvider.Compare(accepted, candidate);
        return IdentityMatches(distance);
    }

    private static bool IdentityMatches(ConversationIdentityComparison comparison) =>
        comparison.IsMatch;

    private static OcrResult OcrFrom(ObservedMessage message) =>
        new(message.NormalizedText, message.OcrConfidence, message.OcrStatus, message.RawText);

    private IReadOnlyList<ObservedMessage> PreviousVisibleMessages()
    {
        var messagesById = _messages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        return _visibleMessages
            .Select(snapshot => messagesById.GetValueOrDefault(snapshot.LogicalMessageId))
            .Where(message => message is not null)
            .Select(message => message!)
            .ToArray();
    }

    private bool CandidateVisuallyMatches(ObservedMessage message, VisibleCandidate candidate) =>
        message.Side == candidate.Bubble.Side &&
        VisualFingerprintsMatch(message.VisualFingerprint, candidate.VisualFingerprint);

    private bool CandidateStronglyVisuallyMatches(ObservedMessage message, VisibleCandidate candidate) =>
        message.Side == candidate.Bubble.Side &&
        VisualFingerprintsStronglyMatch(message.VisualFingerprint, candidate.VisualFingerprint);

    private static bool CandidateTrustedTextMatches(ObservedMessage message, VisibleCandidate candidate) =>
        message.Side == candidate.Bubble.Side &&
        message.IsTrustedForSemantics &&
        candidate.Ocr is
        {
            Status: OcrTextStatus.Recognized,
            Text.Length: > 0,
        } ocr &&
        string.Equals(message.NormalizedText, ocr.Text, StringComparison.Ordinal);

    private bool CandidateStronglyMatches(ObservedMessage message, VisibleCandidate candidate) =>
        CandidateStronglyVisuallyMatches(message, candidate) ||
        CandidateTrustedTextMatches(message, candidate);

    private bool VisualFingerprintsMatch(string accepted, string candidate)
    {
        var distance = PerceptualFingerprint.Distance(
            PerceptualFingerprint.Parse(accepted),
            PerceptualFingerprint.Parse(candidate));
        return distance.HammingDistance <= _options.BubbleMaxHammingDistance &&
               distance.MeanLuminanceDifference <= _options.BubbleMaxMeanLuminanceDifference;
    }

    private bool VisualFingerprintsStronglyMatch(string accepted, string candidate)
    {
        var distance = PerceptualFingerprint.Distance(
            PerceptualFingerprint.Parse(accepted),
            PerceptualFingerprint.Parse(candidate));
        return distance.HammingDistance <= _options.BubbleStrongMaxHammingDistance &&
               distance.MeanLuminanceDifference <= _options.BubbleStrongMaxMeanLuminanceDifference;
    }

    private void HydrateFromPendingSwitch(
        IReadOnlyList<VisibleCandidate> candidates,
        IConversationIdentityEvidence identity)
    {
        if (_pendingSwitch is null || !IdentityMatches(_pendingSwitch.Identity, identity))
        {
            return;
        }

        var pendingMatches = SequenceAlignment.Align(
            _pendingSwitch.Candidates.Count,
            candidates.Count,
            (pending, candidate) =>
                _pendingSwitch.Candidates[pending].Bubble.Side == candidates[candidate].Bubble.Side &&
                VisualFingerprintsMatch(
                    _pendingSwitch.Candidates[pending].VisualFingerprint,
                    candidates[candidate].VisualFingerprint));
        foreach (var (pendingIndex, candidateIndex) in pendingMatches)
        {
            candidates[candidateIndex].Ocr = _pendingSwitch.Candidates[pendingIndex].Ocr;
        }
    }

    private bool CandidateMatches(ObservedMessage message, VisibleCandidate candidate) =>
        message.Side == candidate.Bubble.Side &&
        (VisualFingerprintsMatch(message.VisualFingerprint, candidate.VisualFingerprint) ||
         (!string.IsNullOrWhiteSpace(message.NormalizedText) &&
          string.Equals(message.NormalizedText, candidate.Ocr!.Text, StringComparison.Ordinal)));

    private void InsertInTimeline(
        IReadOnlyList<VisibleCandidate> candidates,
        int candidateIndex,
        ObservedMessage message)
    {
        var previous = candidates.Take(candidateIndex).LastOrDefault(candidate => candidate.Message is not null)?.Message;
        if (previous is not null)
        {
            var previousIndex = _messages.FindIndex(item => item.Id == previous.Id);
            _messages.Insert(previousIndex + 1, message);
            return;
        }

        var next = candidates.Skip(candidateIndex + 1).FirstOrDefault(candidate => candidate.Message is not null)?.Message;
        if (next is not null)
        {
            var nextIndex = _messages.FindIndex(item => item.Id == next.Id);
            _messages.Insert(nextIndex, message);
            return;
        }

        _messages.Add(message);
    }

    private void TrimRecentState()
    {
        while (_messages.Count > _options.RecentMessageLimit)
        {
            _messages.RemoveAt(0);
        }
    }

    private ObservationResult Result(
        bool frameChanged,
        IReadOnlyList<ObservedMessage> observed,
        IReadOnlyList<ObservedMessage> emitted,
        IReadOnlyList<string> duplicates,
        ObserverTimings timings,
        ConversationIdentityObservation identity)
    {
        _messagesEmitted += emitted.Count;
        _duplicatesSuppressed += duplicates.Count;
        foreach (var message in emitted)
        {
            NewMessageObserved?.Invoke(this, new MessageObservedEventArgs(message));
        }

        return new(
            _epoch!,
            frameChanged,
            observed,
            emitted,
            duplicates,
            Counters,
            timings,
            identity);
    }

    private sealed class VisibleCandidate(DetectedBubble bubble, string visualFingerprint)
    {
        public DetectedBubble Bubble { get; } = bubble;

        public string VisualFingerprint { get; } = visualFingerprint;

        public OcrResult? Ocr { get; set; }

        public ObservedMessage? Message { get; set; }
    }

    private sealed record PendingConversationSwitch(
        IConversationIdentityEvidence Identity,
        int Observations,
        IReadOnlyList<PendingCandidate> Candidates);

    private sealed record PendingCandidate(
        DetectedBubble Bubble,
        string VisualFingerprint,
        OcrResult Ocr)
    {
        public static PendingCandidate From(VisibleCandidate candidate) =>
            new(candidate.Bubble, candidate.VisualFingerprint, candidate.Ocr!);
    }
}
