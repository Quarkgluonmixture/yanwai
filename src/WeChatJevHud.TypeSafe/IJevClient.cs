namespace WeChatJevHud.TypeSafe;

/// <summary>
/// Product seam for Jev. Implementations answer a set of narrow questions about
/// one shared conversation state (see docs/DECISIONS.md D-002).
/// </summary>
public interface IJevClient
{
    Task<JevResult> AskAsync(
        string state,
        IReadOnlyList<JevQuestion> questions,
        CancellationToken cancellationToken);
}
