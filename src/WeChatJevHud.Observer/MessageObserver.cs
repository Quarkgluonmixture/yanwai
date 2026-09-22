using System.Diagnostics;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;
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
    private bool _awaitingInitialSnapshot;
    private IReadOnlyList<InitialSnapshotCandidate> _initialSnapshotCandidates = [];
    private int _initialSnapshotObservations;
    private int _emptyBaselineObservations;
    private bool _baselineEstablishedThisFrame;
    private string? _liveTailId;
    private PendingConversationSwitch? _pendingSwitch;
    private bool _layoutTransitionActive;
    private int _layoutStableObservationCount;
    private double _deltaY;
    private double _geometryScale = 1;
    private bool _hasTranslationAnchors;
    private CapturePixelRect _previousMatchRegion;
    private IReadOnlyList<OccurrenceMatchDiagnostic> _occurrenceDiagnostics = [];
    private IReadOnlyList<BubbleVisibilityDiagnostic> _visibilityDiagnostics = [];

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
            _options.EmptyBaselineRequiredObservations < 1 ||
            _options.InitialSnapshotRequiredObservations < 1 ||
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
        _baselineEstablishedThisFrame = false;
        _occurrenceDiagnostics = [];
        _visibilityDiagnostics = [];

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
        var frameChanged = _epoch is null || tentativeIdentityMismatch || _awaitingInitialSnapshot ||
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
            if (_awaitingInitialSnapshot)
            {
                _initialSnapshotCandidates = [];
                _initialSnapshotObservations = 0;
                _emptyBaselineObservations = 0;
            }
        }
        else if (_layoutTransitionActive)
        {
            _layoutStableObservationCount++;
        }

        var isInitialEpoch = _epoch is null;
        if (isInitialEpoch)
        {
            StartEpoch(identity, frame.CapturedAt, awaitInitialSnapshot: false);
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
            layoutChanged,
            identityDistance.TitleVisualDistance,
            identityDistance.TitleAspectDistance);
        if (!identityMismatch && _pendingSwitch is not null)
        {
            _pendingSwitch = null;
            _identitySwitchesSuppressed++;
        }

        frameChanged = isInitialEpoch || identityMismatch || _awaitingInitialSnapshot ||
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
                PixelFingerprint.ComputePerceptual(frame, bubble.Bounds).Signature,
                IsComplete(bubble.Bounds, _chatRegion.Value),
                PixelFingerprint.HashSampled(frame, bubble.Bounds, 8192, includeDimensions: true)))
            .ToArray();
        _visibilityDiagnostics = candidates.Select(c => new BubbleVisibilityDiagnostic(c.Bubble.Bounds, _chatRegion.Value, c.IsFullyVisible)).ToArray();
        _previousMatchRegion = previousChatRegion ?? _chatRegion.Value;
        var previousVisibleMessages = PreviousVisibleMessages();
        EstimateTranslation(previousVisibleMessages, candidates);
        var canReconcileVisibleState = _baselineEstablished ||
                                       (_awaitingInitialSnapshot && _messages.Count > 0);
        var weakHistoryMatches = canReconcileVisibleState && !identityMismatch
            ? AlignHistory(previousVisibleMessages, candidates, CandidateVisuallyMatches)
            : [];
        var strongVisualPreviousMatches = canReconcileVisibleState
            ? SequenceAlignment.Align(
                previousVisibleMessages.Count,
                candidates.Length,
                (message, candidate) =>
                    CandidateStronglyVisuallyMatches(previousVisibleMessages[message], candidates[candidate]),
                (message, candidate) => GeometryCost(previousVisibleMessages[message], candidates[candidate]))
            : [];
        var weakVisualPreviousMatches = canReconcileVisibleState
            ? SequenceAlignment.Align(
                previousVisibleMessages.Count,
                candidates.Length,
                (message, candidate) =>
                    CandidateVisuallyMatches(previousVisibleMessages[message], candidates[candidate]),
                (message, candidate) => GeometryCost(previousVisibleMessages[message], candidates[candidate]))
            : [];
        foreach (var (messageIndex, candidateIndex) in weakHistoryMatches)
        {
            if (CanReuseText(_messages[messageIndex], candidates[candidateIndex]))
                candidates[candidateIndex].Ocr = OcrFrom(_messages[messageIndex]);
        }

        var hasStrongVisualContinuity = strongVisualPreviousMatches.Count >= 2;
        var titleAllowsVisualReuse = !identityMismatch || identityDistance.TitleVisualDistance is null ||
            (strongVisualPreviousMatches.Count >= 3 &&
             strongVisualPreviousMatches.Count >= Math.Ceiling(Math.Max(previousVisibleMessages.Count, candidates.Length) * .8) &&
             strongVisualPreviousMatches.Select(m => previousVisibleMessages[m.Left].VisualFingerprint).Distinct().Count() >= 2);
        if (hasStrongVisualContinuity && titleAllowsVisualReuse)
        {
            foreach (var (messageIndex, candidateIndex) in strongVisualPreviousMatches)
            {
                if (CanReuseText(previousVisibleMessages[messageIndex], candidates[candidateIndex]))
                    candidates[candidateIndex].Ocr = OcrFrom(previousVisibleMessages[messageIndex]);
            }
        }

        HydrateFromPendingSwitch(candidates, identity);

        var strongVisualLiveTailMatched = strongVisualPreviousMatches.Any(match =>
            string.Equals(previousVisibleMessages[match.Left].Id, _liveTailId, StringComparison.Ordinal));
        var weakVisualLiveTailMatched = weakVisualPreviousMatches.Any(match =>
            string.Equals(previousVisibleMessages[match.Left].Id, _liveTailId, StringComparison.Ordinal));
        var weakOnlyVisualPreviousOverlap = CountWeakOnlyMatches(
            weakVisualPreviousMatches,
            strongVisualPreviousMatches);
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
                PreviousVisibleWeakOverlap = weakOnlyVisualPreviousOverlap,
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
            if (!candidate.IsFullyVisible)
            {
                candidate.Ocr = new OcrResult("", null, OcrTextStatus.NoText, "");
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var ocrTimer = Stopwatch.StartNew();
            candidate.Ocr = await _ocrEngine.RecognizeAsync(
                new ImageCrop(frame, candidate.Bubble.Bounds),
                cancellationToken).ConfigureAwait(false);
            candidate.HasIndependentOcrEvidence = true;
            ocrTimer.Stop();
            ocrDuration += ocrTimer.Elapsed;
            _ocrCalls++;
        }

        reconcileTimer.Start();
        var matches = canReconcileVisibleState
            ? AlignHistory(previousVisibleMessages, candidates, CandidateMatches)
            : [];
        var trustedTextPreviousMatches = canReconcileVisibleState
            ? SequenceAlignment.Align(
                previousVisibleMessages.Count,
                candidates.Length,
                (message, candidate) =>
                    CandidateTrustedTextMatches(previousVisibleMessages[message], candidates[candidate]))
            : [];
        var strongPreviousMatches = canReconcileVisibleState
            ? SequenceAlignment.Align(
                previousVisibleMessages.Count,
                candidates.Length,
                (message, candidate) =>
                    CandidateStronglyMatches(previousVisibleMessages[message], candidates[candidate]))
            : [];
        var trustedTextLiveTailMatched = trustedTextPreviousMatches.Any(match =>
            string.Equals(previousVisibleMessages[match.Left].Id, _liveTailId, StringComparison.Ordinal));
        var strongVisualLiveTailHasOrderedContinuity = strongVisualLiveTailMatched &&
                                                       strongVisualPreviousMatches.Count >= 2;
        var liveTailStrongMatched = trustedTextLiveTailMatched || strongVisualLiveTailHasOrderedContinuity;
        var hasStrongPreviousVisibleContinuity =
            strongPreviousMatches.Count >= 2 ||
            liveTailStrongMatched ||
            (_layoutTransitionActive && trustedTextPreviousMatches.Count >= 1);
        // A demonstrably different title at a stable layout cannot be overridden by
        // a couple of common short bubbles. Require broad previous-visible continuity.
        if (identityMismatch && identityDistance.TitleVisualDistance is not null && !_layoutTransitionActive)
            hasStrongPreviousVisibleContinuity &= strongPreviousMatches.Count >= 3 &&
                strongPreviousMatches.Count >= Math.Ceiling(Math.Max(previousVisibleMessages.Count, candidates.Length) * .8) &&
                strongPreviousMatches.Select(m => previousVisibleMessages[m.Left].VisualFingerprint).Distinct().Count() >= 2;
        var weakOnlyPreviousOverlap = CountWeakOnlyMatches(
            weakVisualPreviousMatches,
            strongPreviousMatches);
        var liveTailWeakMatched = weakVisualLiveTailMatched && !liveTailStrongMatched;
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
                    PreviousVisibleWeakOverlap = weakOnlyPreviousOverlap,
                    TrustedTextOverlap = trustedTextPreviousMatches.Count,
                    LiveTailStrongMatch = liveTailStrongMatched,
                    LiveTailWeakMatch = liveTailWeakMatched,
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
                        PreviousVisibleWeakOverlap = weakOnlyPreviousOverlap,
                        TrustedTextOverlap = trustedTextPreviousMatches.Count,
                        LiveTailStrongMatch = liveTailStrongMatched,
                        LiveTailWeakMatch = liveTailWeakMatched,
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

                StartEpoch(identity, frame.CapturedAt, awaitInitialSnapshot: true);
                _pendingSwitch = null;
                _layoutTransitionActive = false;
                _identitySwitchesConfirmed++;
                identityObservation = identityObservation with
                {
                    Decision = ConversationIdentityDecision.ConfirmedSwitch,
                    PreviousVisibleStrongOverlap = strongPreviousMatches.Count,
                    PreviousVisibleWeakOverlap = weakOnlyPreviousOverlap,
                    TrustedTextOverlap = trustedTextPreviousMatches.Count,
                    LiveTailStrongMatch = liveTailStrongMatched,
                    LiveTailWeakMatch = liveTailWeakMatched,
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
        var geometryAnchoredAppend = !layoutChanged && !identityMismatch && previousVisibleMessages.Count > 0 &&
            previousVisibleMessages.All(previous => matches.Any(match => _messages[match.Left].Id == previous.Id &&
                GeometryCost(previous, candidates[match.Right]) <= .15)) &&
            Math.Abs(_deltaY) <= 1 && Math.Abs(_geometryScale - 1) <= .02;
        _occurrenceDiagnostics = matches.Where(match => previousVisibleIds.Contains(_messages[match.Left].Id))
            .Select(match => new OccurrenceMatchDiagnostic(_messages[match.Left].Id, _messages[match.Left].BubbleRect.Y,
                candidates[match.Right].Bubble.Bounds.Y, _deltaY, GeometryCost(_messages[match.Left], candidates[match.Right]),
                candidates.Count(c => CandidateMatches(_messages[match.Left], c)))).ToArray();
        for (var index = 0; index < _messages.Count; index++)
        {
            _messages[index] = _messages[index] with { IsVisible = false };
        }

        var duplicateIds = new List<string>(matches.Count);
        var completedMessages = new List<ObservedMessage>();
        foreach (var (messageIndex, candidateIndex) in matches)
        {
            var messageId = _messages[messageIndex].Id;
            var currentIndex = _messages.FindIndex(message => message.Id == messageId);
            var updated = _messages[currentIndex] with
            {
                BubbleRect = candidates[candidateIndex].Bubble.Bounds,
                VisualFingerprint = candidates[candidateIndex].VisualFingerprint,
                IsVisible = true,
                IsFullyVisible = candidates[candidateIndex].IsFullyVisible,
            };
            if (!updated.HasCompleteText && candidates[candidateIndex].IsFullyVisible)
            {
                var completeOcr = candidates[candidateIndex].Ocr!;
                updated = updated with
                {
                    HasCompleteText = true,
                    RawText = completeOcr.RawText,
                    NormalizedText = completeOcr.Text,
                    OcrStatus = completeOcr.Status,
                    OcrConfidence = completeOcr.OcrConfidence,
                    OcrDiagnostics = completeOcr.Diagnostics,
                    CompleteCropFingerprint = candidates[candidateIndex].CropFingerprint
                };
                completedMessages.Add(updated);
            }
            else if (candidates[candidateIndex] is { IsFullyVisible: true, HasIndependentOcrEvidence: true } verified &&
                     string.Equals(updated.NormalizedText, verified.Ocr?.Text, StringComparison.Ordinal))
            {
                updated = updated with { CompleteCropFingerprint = verified.CropFingerprint };
            }
            _messages[currentIndex] = updated;
            candidates[candidateIndex].Message = updated;
            duplicateIds.Add(updated.Id);
        }

        var observed = new List<ObservedMessage>(candidates.Length - matches.Count);
        observed.AddRange(completedMessages);
        var emitted = new List<ObservedMessage>();
        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            if (matchedByCandidate.ContainsKey(candidateIndex))
            {
                continue;
            }

            var candidate = candidates[candidateIndex];
            var hasDistinctAnchor = matches.Any(match =>
                !CandidateMatches(candidates[match.Right].Message!, candidate));
            var origin = !_baselineEstablished
                ? MessageObservationKind.Bootstrap
                : timelineWasEmpty ||
                  (liveTailCandidateIndex is { } liveTail && candidateIndex > liveTail && (hasDistinctAnchor || geometryAnchoredAppend))
                    ? MessageObservationKind.LiveNew
                    : MessageObservationKind.History;
            if (!candidate.IsFullyVisible && origin == MessageObservationKind.LiveNew)
                origin = MessageObservationKind.History;
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
                IsVisible: true,
                OcrDiagnostics: ocr.Diagnostics,
                IsFullyVisible: candidate.IsFullyVisible,
                HasCompleteText: candidate.IsFullyVisible,
                CompleteCropFingerprint: candidate.IsFullyVisible ? candidate.CropFingerprint : null);
            observed.Add(message);
            candidate.Message = message;
            InsertInTimeline(candidates, candidateIndex, message);
            if (origin == MessageObservationKind.LiveNew)
            {
                emitted.Add(message);
                _liveTailId = message.Id;
            }
        }

        AdvanceBaseline(candidates);
        if ((_liveTailId is null || _awaitingInitialSnapshot || _baselineEstablishedThisFrame) &&
            candidates.LastOrDefault()?.Message is { } baselineTail)
        {
            _liveTailId = baselineTail.Id;
        }

        _visibleMessages.Clear();
        _visibleMessages.AddRange(candidates.Select(candidate => new VisibleMessageSnapshot(
            candidate.Message!.Id,
            candidate.Message.Side,
            candidate.Bubble.Bounds,
            candidate.VisualFingerprint,
            candidate.IsFullyVisible)));
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

    private static bool IsComplete(CapturePixelRect bubble, CapturePixelRect roi)
    {
        var margin = BoundaryMargin(roi);
        return bubble.Y > roi.Y + margin && bubble.Bottom < roi.Bottom - margin;
    }

    private static int BoundaryMargin(CapturePixelRect roi) => Math.Max(1, roi.Height / 500);

    private static bool CanReuseText(ObservedMessage message, VisibleCandidate candidate) =>
        message.HasCompleteText && candidate.IsFullyVisible &&
        (message.IsFullyVisible || message.CompleteCropFingerprint == candidate.CropFingerprint);

    private void EstimateTranslation(IReadOnlyList<ObservedMessage> previous, IReadOnlyList<VisibleCandidate> candidates)
    {
        var anchors = new List<(ObservedMessage Previous, VisibleCandidate Current)>();
        foreach (var message in previous.Where(m => m.IsFullyVisible))
        {
            var compatible = candidates.Where(c => CandidateStronglyVisuallyMatches(message, c)).ToArray();
            if (compatible.Length == 1 && previous.Count(m => CandidateStronglyVisuallyMatches(m, compatible[0])) == 1)
                anchors.Add((message, compatible[0]));
        }
        _geometryScale = anchors.Count == 0 ? 1 : Median(anchors.Select(a => a.Current.Bubble.Bounds.Height / (double)a.Previous.BubbleRect.Height));
        _hasTranslationAnchors = anchors.Count > 0;
        _deltaY = anchors.Count == 0 ? 0 : Median(anchors.Select(a => a.Current.Bubble.Bounds.Y - a.Previous.BubbleRect.Y * _geometryScale));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    private double GeometryCost(ObservedMessage previous, VisibleCandidate candidate) =>
        Math.Abs(candidate.Bubble.Bounds.Y - (previous.BubbleRect.Y * _geometryScale + _deltaY)) /
        Math.Max(1, previous.BubbleRect.Height * _geometryScale);

    private bool PartialGeometryMatches(ObservedMessage previous, VisibleCandidate candidate)
    {
        if (previous.IsFullyVisible && candidate.IsFullyVisible) return false;
        if (!_hasTranslationAnchors) return false;
        if (previous.Side != candidate.Bubble.Side) return false;
        var a = previous.BubbleRect;
        var b = candidate.Bubble.Bounds;
        var tolerance = Math.Max(2, Math.Min(a.Height, b.Height) * .12);
        if (Math.Abs(a.Width * _geometryScale - b.Width) > tolerance ||
            Math.Abs(a.X * _geometryScale - b.X) > tolerance) return false;
        // Compare the surviving edge, not a clipped crop's perceptual hash or OCR.
        var topClipped = !previous.IsFullyVisible && a.Y <= _previousMatchRegion.Y + BoundaryMargin(_previousMatchRegion) ||
                         !candidate.IsFullyVisible && b.Y <= _chatRegion!.Value.Y + BoundaryMargin(_chatRegion.Value);
        return topClipped
            ? Math.Abs(a.Bottom * _geometryScale + _deltaY - b.Bottom) <= tolerance
            : Math.Abs(a.Y * _geometryScale + _deltaY - b.Y) <= tolerance;
    }

    private IReadOnlyList<(int Left, int Right)> AlignHistory(
        IReadOnlyList<ObservedMessage> previous,
        IReadOnlyList<VisibleCandidate> candidates,
        Func<ObservedMessage, VisibleCandidate, bool> predicate)
    {
        // Lock previous-visible occurrences first; only then fill chronological gaps from history.
        var visible = SequenceAlignment.Align(previous.Count, candidates.Count,
            (m, c) => predicate(previous[m], candidates[c]),
            (m, c) => GeometryCost(previous[m], candidates[c]));
        var anchors = visible.Select(pair => (Left: _messages.FindIndex(m => m.Id == previous[pair.Left].Id), Right: pair.Right)).ToArray();
        var result = new List<(int Left, int Right)>();
        var leftStart = 0;
        var rightStart = 0;
        foreach (var anchor in anchors.Append((_messages.Count, candidates.Count)))
        {
            var left = anchor.Item1;
            var right = anchor.Item2;
            if (left >= leftStart && right >= rightStart)
            {
                result.AddRange(SequenceAlignment.Align(left - leftStart, right - rightStart,
                    (m, c) => predicate(_messages[leftStart + m], candidates[rightStart + c]))
                    .Select(pair => (pair.Left + leftStart, pair.Right + rightStart)));
            }
            if (left < _messages.Count) result.Add((left, right));
            leftStart = left + 1;
            rightStart = right + 1;
        }
        return result;
    }

    private void StartEpoch(
        IConversationIdentityEvidence identity,
        DateTimeOffset observedAt,
        bool awaitInitialSnapshot)
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
        _awaitingInitialSnapshot = awaitInitialSnapshot;
        _initialSnapshotCandidates = [];
        _initialSnapshotObservations = 0;
        _emptyBaselineObservations = 0;
        _liveTailId = null;
        _pendingSwitch = null;
        ConversationChanged?.Invoke(this, new ConversationChangedEventArgs(previous, _epoch));
    }

    private void AdvanceBaseline(IReadOnlyList<VisibleCandidate> candidates)
    {
        if (!_awaitingInitialSnapshot)
        {
            _baselineEstablishedThisFrame = !_baselineEstablished;
            _baselineEstablished = true;
            return;
        }

        if (candidates.Count > 0)
        {
            _emptyBaselineObservations = 0;
            var currentSnapshot = candidates
                .Select(candidate => new InitialSnapshotCandidate(
                    candidate.Bubble.Side,
                    candidate.VisualFingerprint))
                .ToArray();
            _initialSnapshotObservations = InitialSnapshotMatches(currentSnapshot)
                ? _initialSnapshotObservations + 1
                : 1;
            _initialSnapshotCandidates = currentSnapshot;
            if (_initialSnapshotObservations < _options.InitialSnapshotRequiredObservations)
            {
                return;
            }

            _awaitingInitialSnapshot = false;
            _baselineEstablished = true;
            _baselineEstablishedThisFrame = true;
            return;
        }

        _initialSnapshotCandidates = [];
        _initialSnapshotObservations = 0;
        if (_layoutTransitionActive)
        {
            return;
        }

        _emptyBaselineObservations++;
        if (_emptyBaselineObservations < _options.EmptyBaselineRequiredObservations)
        {
            return;
        }

        _awaitingInitialSnapshot = false;
        _baselineEstablished = true;
        _baselineEstablishedThisFrame = true;
        _messages.Clear();
        _visibleMessages.Clear();
        _liveTailId = null;
    }

    private bool InitialSnapshotMatches(IReadOnlyList<InitialSnapshotCandidate> candidates) =>
        _initialSnapshotCandidates.Count == candidates.Count &&
        _initialSnapshotCandidates
            .Zip(candidates)
            .All(pair =>
                pair.First.Side == pair.Second.Side &&
                VisualFingerprintsStronglyMatch(
                    pair.First.VisualFingerprint,
                    pair.Second.VisualFingerprint));

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
        new(
            message.NormalizedText,
            message.OcrConfidence,
            message.OcrStatus,
            message.RawText,
            message.OcrDiagnostics);

    private IReadOnlyList<ObservedMessage> PreviousVisibleMessages()
    {
        var messagesById = _messages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        return _visibleMessages
            .Select(snapshot => messagesById.GetValueOrDefault(snapshot.LogicalMessageId))
            .Where(message => message is not null)
            .Select(message => message!)
            .ToArray();
    }

    private static int CountWeakOnlyMatches(
        IReadOnlyList<(int Left, int Right)> weakMatches,
        IReadOnlyList<(int Left, int Right)> strongMatches)
    {
        var strongPairs = strongMatches.ToHashSet();
        return weakMatches.Count(match => !strongPairs.Contains(match));
    }

    private bool CandidateVisuallyMatches(ObservedMessage message, VisibleCandidate candidate) =>
        message.Side == candidate.Bubble.Side &&
        (PartialGeometryMatches(message, candidate) ||
         (message.IsFullyVisible && candidate.IsFullyVisible && VisualFingerprintsMatch(message.VisualFingerprint, candidate.VisualFingerprint)));

    private bool CandidateStronglyVisuallyMatches(ObservedMessage message, VisibleCandidate candidate) =>
        message.Side == candidate.Bubble.Side &&
        message.IsFullyVisible && candidate.IsFullyVisible &&
        VisualFingerprintsStronglyMatch(message.VisualFingerprint, candidate.VisualFingerprint);

    private static bool CandidateTrustedTextMatches(ObservedMessage message, VisibleCandidate candidate) =>
        message.Side == candidate.Bubble.Side &&
        candidate.IsFullyVisible &&
        message.IsTrustedForSemantics &&
        candidate.HasIndependentOcrEvidence &&
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
                _pendingSwitch.Candidates[pending].IsFullyVisible == candidates[candidate].IsFullyVisible &&
                _pendingSwitch.Candidates[pending].Bubble.Side == candidates[candidate].Bubble.Side &&
                VisualFingerprintsMatch(
                    _pendingSwitch.Candidates[pending].VisualFingerprint,
                    candidates[candidate].VisualFingerprint));
        foreach (var (pendingIndex, candidateIndex) in pendingMatches)
        {
            candidates[candidateIndex].Ocr = _pendingSwitch.Candidates[pendingIndex].Ocr;
            candidates[candidateIndex].HasIndependentOcrEvidence =
                _pendingSwitch.Candidates[pendingIndex].HasIndependentOcrEvidence;
        }
    }

    private bool CandidateMatches(ObservedMessage message, VisibleCandidate candidate) =>
        message.Side == candidate.Bubble.Side &&
        (!message.HasCompleteText || message.IsFullyVisible || !candidate.IsFullyVisible ||
         message.CompleteCropFingerprint == candidate.CropFingerprint ||
         (candidate.HasIndependentOcrEvidence && message.NormalizedText == candidate.Ocr?.Text)) &&
        (CandidateVisuallyMatches(message, candidate) ||
         (message.HasCompleteText && candidate.IsFullyVisible && !string.IsNullOrWhiteSpace(message.NormalizedText) &&
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
            identity,
            new ConversationBaselineObservation(
                _awaitingInitialSnapshot
                    ? ConversationBaselineState.AwaitingInitialSnapshot
                    : ConversationBaselineState.Established,
                _initialSnapshotObservations,
                _options.InitialSnapshotRequiredObservations,
                _emptyBaselineObservations,
                _options.EmptyBaselineRequiredObservations,
                _baselineEstablishedThisFrame),
            _occurrenceDiagnostics,
            _visibilityDiagnostics);
    }

    private sealed class VisibleCandidate(DetectedBubble bubble, string visualFingerprint, bool isFullyVisible, string cropFingerprint)
    {
        public string CropFingerprint { get; } = cropFingerprint;
        public bool IsFullyVisible { get; } = isFullyVisible;
        public DetectedBubble Bubble { get; } = bubble;

        public string VisualFingerprint { get; } = visualFingerprint;

        public OcrResult? Ocr { get; set; }

        public bool HasIndependentOcrEvidence { get; set; }

        public ObservedMessage? Message { get; set; }
    }

    private sealed record InitialSnapshotCandidate(
        MessageSide Side,
        string VisualFingerprint);

    private sealed record PendingConversationSwitch(
        IConversationIdentityEvidence Identity,
        int Observations,
        IReadOnlyList<PendingCandidate> Candidates);

    private sealed record PendingCandidate(
        DetectedBubble Bubble,
        string VisualFingerprint,
        OcrResult Ocr,
        bool HasIndependentOcrEvidence,
        bool IsFullyVisible)
    {
        public static PendingCandidate From(VisibleCandidate candidate) =>
            new(
                candidate.Bubble,
                candidate.VisualFingerprint,
                candidate.Ocr!,
                candidate.HasIndependentOcrEvidence,
                candidate.IsFullyVisible);
    }
}
