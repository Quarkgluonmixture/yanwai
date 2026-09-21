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
    private bool _baselineEstablished;
    private string? _liveTailId;

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
        _conversationSwitches);

    public async Task<ObservationResult> ObserveAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var frameTimer = Stopwatch.StartNew();
        _framesChecked++;

        if (_chatRegion is null || _frameWidth != frame.Width || _frameHeight != frame.Height)
        {
            _chatRegion = _chatRegionLocator.Locate(frame).Bounds;
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
        }

        var changeTimer = Stopwatch.StartNew();
        var signature = _conversationIdentityProvider.GetVisualSignature(frame, _chatRegion.Value);
        var isNewEpoch = _epoch is null || !string.Equals(_epoch.VisualSignature, signature, StringComparison.Ordinal);
        var frameFingerprint = _changeDetector.ComputeFingerprint(frame, _chatRegion.Value);
        var frameChanged = isNewEpoch || !string.Equals(_lastFrameFingerprint, frameFingerprint, StringComparison.Ordinal);
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
                new ObserverTimings(frameCheckDuration, changeTimer.Elapsed, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        }

        _changedFrames++;
        if (isNewEpoch)
        {
            StartEpoch(signature, frame.CapturedAt);
        }

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
                PixelFingerprint.HashSampled(
                    frame,
                    bubble.Bounds,
                    targetSamples: 2_048,
                    includeDimensions: false)))
            .ToArray();
        var primaryMatches = _baselineEstablished
            ? SequenceAlignment.Align(
                _messages.Count,
                candidates.Length,
                (message, candidate) =>
                    _messages[message].Side == candidates[candidate].Bubble.Side &&
                    string.Equals(
                        _messages[message].VisualFingerprint,
                        candidates[candidate].VisualFingerprint,
                        StringComparison.Ordinal))
            : [];
        foreach (var (messageIndex, candidateIndex) in primaryMatches)
        {
            candidates[candidateIndex].Ocr = OcrFrom(_messages[messageIndex]);
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
            new ObserverTimings(frameCheckDuration, changeTimer.Elapsed, bubbleTimer.Elapsed, ocrDuration, reconcileTimer.Elapsed));
    }

    private void StartEpoch(string signature, DateTimeOffset observedAt)
    {
        var previous = _epoch;
        if (previous is not null)
        {
            _conversationSwitches++;
        }

        _epoch = new ConversationEpoch((previous?.Id ?? 0) + 1, signature, observedAt);
        _messages.Clear();
        _visibleMessages.Clear();
        _lastFrameFingerprint = null;
        _baselineEstablished = false;
        _liveTailId = null;
        ConversationChanged?.Invoke(this, new ConversationChangedEventArgs(previous, _epoch));
    }

    private static OcrResult OcrFrom(ObservedMessage message) =>
        new(message.NormalizedText, message.OcrConfidence, message.OcrStatus, message.RawText);

    private static bool CandidateMatches(ObservedMessage message, VisibleCandidate candidate) =>
        message.Side == candidate.Bubble.Side &&
        (string.Equals(message.VisualFingerprint, candidate.VisualFingerprint, StringComparison.Ordinal) ||
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
        ObserverTimings timings)
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
            timings);
    }

    private sealed class VisibleCandidate(DetectedBubble bubble, string visualFingerprint)
    {
        public DetectedBubble Bubble { get; } = bubble;

        public string VisualFingerprint { get; } = visualFingerprint;

        public OcrResult? Ocr { get; set; }

        public ObservedMessage? Message { get; set; }
    }
}
