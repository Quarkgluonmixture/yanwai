namespace Yanwai.TypeSafe;

public abstract record JevAnswer(string Key);

public sealed record NoulAnswer(string Key, double Probability) : JevAnswer(Key);

public sealed record ChoiceAnswer(
    string Key,
    string Choice,
    IReadOnlyList<ChoiceProbability> Probabilities,
    double? Confidence) : JevAnswer(Key);

public sealed record ChoiceProbability(string Key, double Probability);

public sealed record ScoreAnswer(
    string Key,
    double Score,
    IReadOnlyList<string> Legend,
    IReadOnlyList<double> Probabilities,
    double? Confidence) : JevAnswer(Key)
{
    /// <summary>The level the weighted mean is nearest to, or null when the legend is empty.</summary>
    public string? NearestLevel =>
        Legend.Count == 0
            ? null
            : Legend[Math.Clamp((int)Math.Round(Score), 0, Legend.Count - 1)];
}

public sealed record JevUsage(int InputTokens, int OutputTokens);

public sealed record JevResult(
    string Model,
    IReadOnlyDictionary<string, JevAnswer> Answers,
    JevUsage Usage,
    TimeSpan Latency);
