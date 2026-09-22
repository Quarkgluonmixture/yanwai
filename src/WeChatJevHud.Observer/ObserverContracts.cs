using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Ocr;

namespace WeChatJevHud.Observer;

public interface IMessageObserver
{
    event EventHandler<ConversationChangedEventArgs>? ConversationChanged;

    event EventHandler<MessageObservedEventArgs>? MessageObserved;

    event EventHandler<MessageObservedEventArgs>? NewMessageObserved;

    RecentConversationSnapshot State { get; }

    ObserverCounters Counters { get; }

    Task<ObservationResult> ObserveAsync(CapturedFrame frame, CancellationToken cancellationToken);
}

public interface IChatRoiChangeDetector
{
    string ComputeFingerprint(CapturedFrame frame, CapturePixelRect chatRegion);
}

public interface IConversationIdentityProvider
{
    IConversationIdentityEvidence GetVisualEvidence(CapturedFrame frame, CapturePixelRect chatRegion);

    ConversationIdentityComparison Compare(
        IConversationIdentityEvidence accepted,
        IConversationIdentityEvidence candidate);
}

public interface IConversationIdentityEvidence
{
}

public sealed record ObserverOptions(
    int RecentMessageLimit = 25,
    int PendingSwitchRequiredObservations = 3,
    int LayoutStableObservations = 2,
    int EmptyBaselineRequiredObservations = 3,
    int InitialSnapshotRequiredObservations = 2,
    int BubbleMaxHammingDistance = 20,
    int BubbleMaxMeanLuminanceDifference = 8,
    int BubbleStrongMaxHammingDistance = 8,
    int BubbleStrongMaxMeanLuminanceDifference = 3);

public sealed record ConversationIdentityComparison(
    bool IsMatch,
    string Diagnostics,
    double? TitleVisualDistance = null,
    double? TitleAspectDistance = null);

public sealed record ConversationEpoch(
    long Id,
    IConversationIdentityEvidence VisualIdentity,
    DateTimeOffset StartedAt);

public enum MessageObservationKind
{
    Bootstrap,
    History,
    LiveNew,
}

public enum ConversationIdentityDecision
{
    Initial,
    Same,
    RebaseSameConversation,
    LayoutTransition,
    PendingSwitch,
    ConfirmedSwitch,
}

public enum ConversationBaselineState
{
    AwaitingInitialSnapshot,
    Established,
}

public sealed record ConversationBaselineObservation(
    ConversationBaselineState State,
    int InitialSnapshotObservations,
    int RequiredInitialSnapshotObservations,
    int EmptyObservations,
    int RequiredEmptyObservations,
    bool EstablishedThisFrame);

public sealed record ConversationIdentityObservation(
    ConversationIdentityDecision Decision,
    bool CandidateChanged,
    string ProviderDiagnostics,
    int PreviousVisibleStrongOverlap,
    int PreviousVisibleWeakOverlap,
    int TrustedTextOverlap,
    bool LiveTailStrongMatch,
    bool LiveTailWeakMatch,
    int HistoryOnlyMatches,
    int VisibleCandidates,
    int PendingObservations,
    int RequiredObservations,
    bool LayoutChanged,
    double? TitleVisualDistance = null,
    double? TitleAspectDistance = null);

public sealed record ObservedMessage(
    string Id,
    long ConversationEpochId,
    MessageSide Side,
    string NormalizedText,
    string RawText,
    OcrTextStatus OcrStatus,
    double? OcrConfidence,
    CapturePixelRect BubbleRect,
    DateTimeOffset FirstObservedAt,
    MessageObservationKind Origin,
    string VisualFingerprint,
    bool IsVisible,
    string? QuotedText = null,
    CapturePixelRect? QuotedRegion = null,
    OcrDiagnostics? OcrDiagnostics = null,
    bool IsFullyVisible = true,
    bool HasCompleteText = true,
    string? CompleteCropFingerprint = null)
{
    public bool IsTrustedForSemantics =>
        IsFullyVisible && HasCompleteText && OcrStatus == OcrTextStatus.Recognized &&
        !string.IsNullOrWhiteSpace(NormalizedText);
}

public sealed record VisibleMessageSnapshot(
    string LogicalMessageId,
    MessageSide Side,
    CapturePixelRect BubbleRect,
    string VisualFingerprint,
    bool IsFullyVisible = true,
    string? CropFingerprint = null);

public sealed record RecentConversationSnapshot(
    ConversationEpoch? Epoch,
    IReadOnlyList<ObservedMessage> Messages,
    IReadOnlyList<VisibleMessageSnapshot> VisibleMessages);

public sealed record ObserverCounters(
    long FramesChecked,
    long UnchangedFrames,
    long ChangedFrames,
    long BubbleDetectionRuns,
    long OcrCalls,
    long MessagesEmitted,
    long DuplicatesSuppressed,
    long ConversationSwitches,
    long IdentityMismatchCandidates,
    long IdentityRebases,
    long IdentitySwitchesConfirmed,
    long IdentitySwitchesSuppressed,
    long LayoutTransitions);

public sealed record ObserverTimings(
    TimeSpan FrameCheck,
    TimeSpan ChangeDetect,
    TimeSpan BubbleDetect,
    TimeSpan Ocr,
    TimeSpan ObserverReconcile);

public sealed record ObservationResult(
    ConversationEpoch Epoch,
    bool FrameChanged,
    IReadOnlyList<ObservedMessage> MessagesObserved,
    IReadOnlyList<ObservedMessage> NewMessages,
    IReadOnlyList<string> DuplicateMessageIds,
    ObserverCounters Counters,
    ObserverTimings Timings,
    ConversationIdentityObservation Identity,
    ConversationBaselineObservation Baseline,
    IReadOnlyList<OccurrenceMatchDiagnostic>? OccurrenceMatches = null,
    IReadOnlyList<BubbleVisibilityDiagnostic>? BubbleVisibility = null,
    LiveEdgeAppendDecision? LiveEdgeAppend = null);

public sealed record OccurrenceMatchDiagnostic(string PreviousId, int PreviousY, int CandidateY,
    double EstimatedDeltaY, double MatchCost, int AmbiguousOccurrenceCount);

public sealed record BubbleVisibilityDiagnostic(CapturePixelRect BubbleBounds, CapturePixelRect ChatRoi, bool IsFullyVisible);

public sealed class ConversationChangedEventArgs(
    ConversationEpoch? previousEpoch,
    ConversationEpoch currentEpoch) : EventArgs
{
    public ConversationEpoch? PreviousEpoch { get; } = previousEpoch;

    public ConversationEpoch CurrentEpoch { get; } = currentEpoch;
}

public sealed class MessageObservedEventArgs(ObservedMessage message) : EventArgs
{
    public ObservedMessage Message { get; } = message;
}
