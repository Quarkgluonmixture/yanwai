namespace WeChatJevHud.TypeSafe;

/// <summary>
/// Product seam for the later Jev phase. No transport or version-dependent
/// TypeSafe contract is implemented until the Phase 5 live-docs gate.
/// </summary>
public interface IJevClient
{
    Task<JevAnalysisResult> AnalyzeAsync(JevAnalysisInput input, CancellationToken cancellationToken);
}

public sealed record JevAnalysisInput(
    string CurrentMessage,
    IReadOnlyList<string> RecentMessages,
    string Locale);

public sealed record JevAnalysisResult(IReadOnlyList<JevJudgment> Judgments);

public sealed record JevJudgment(string Name, string Value, double Probability);
