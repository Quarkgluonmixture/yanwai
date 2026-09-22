namespace Yanwai.TypeSafe;

/// <summary>
/// One System One question. <see cref="Key"/> is the caller-chosen identifier the
/// answer comes back under; <see cref="Label"/> is the short heading a UI shows.
/// </summary>
public abstract record JevQuestion(string Key, string Label, string Instructions);

/// <summary>Yes/no question. The answer is a calibrated probability.</summary>
public sealed record NoulQuestion(string Key, string Label, string Instructions)
    : JevQuestion(Key, Label, Instructions);

/// <summary>
/// Single-select question. Always include an explicit "none of these" option:
/// Jev must pick one of the supplied keys, so without an escape hatch an unmatched
/// message is forced into the nearest option and the probability then reports a
/// fallback as if it were a decision.
/// </summary>
public sealed record ChoiceQuestion(
    string Key,
    string Label,
    string Instructions,
    IReadOnlyList<ChoiceOption> Options) : JevQuestion(Key, Label, Instructions)
{
    public string LabelFor(string optionKey)
    {
        foreach (var option in Options)
        {
            if (string.Equals(option.Key, optionKey, StringComparison.Ordinal))
            {
                return option.Label;
            }
        }

        return optionKey;
    }
}

public sealed record ChoiceOption(string Key, string Label, string Description);

/// <summary>Ordered levels. The answer is a weighted mean of the level indices.</summary>
public sealed record ScoreQuestion(
    string Key,
    string Label,
    string Instructions,
    IReadOnlyList<string> Levels) : JevQuestion(Key, Label, Instructions);
