using Yanwai.TypeSafe;

namespace Yanwai.Panel;

/// <summary>One bar in a card. <see cref="BarWidth"/> is pre-computed in pixels so
/// the view needs no value converter.</summary>
public sealed class JudgmentRow
{
    public const double MaxBarWidth = 150;

    public JudgmentRow(string label, double probability, bool highlight, bool hasProbability = true)
    {
        Label = label;
        Probability = probability;
        Highlight = highlight;
        HasProbability = hasProbability;
    }

    public string Label { get; }

    public double Probability { get; }

    public bool Highlight { get; }

    /// <summary>False when the server sent no distribution for this question.</summary>
    public bool HasProbability { get; }

    public string ProbabilityText => HasProbability ? $"{Probability * 100:N0}%" : "—";

    public double BarWidth => Math.Max(1, Math.Clamp(Probability, 0, 1) * MaxBarWidth);
}

public sealed class JudgmentCard
{
    public JudgmentCard(string title, string? summary, IReadOnlyList<JudgmentRow> rows)
    {
        Title = title;
        Summary = summary;
        Rows = rows;
    }

    public string Title { get; }

    /// <summary>Optional one-line readout shown next to the title (score levels, mostly).</summary>
    public string? Summary { get; }

    public bool HasSummary => !string.IsNullOrEmpty(Summary);

    public IReadOnlyList<JudgmentRow> Rows { get; }

    /// <summary>
    /// Turns one answer into a card. Unanswered questions are skipped by the caller;
    /// this method never invents a value for a missing answer.
    /// </summary>
    public static JudgmentCard FromAnswer(JevQuestion question, JevAnswer answer)
    {
        switch (answer)
        {
            case NoulAnswer noul:
                return new JudgmentCard(
                    question.Label,
                    null,
                    new[]
                    {
                        new JudgmentRow("是", noul.Probability, noul.Probability >= 0.5),
                        new JudgmentRow("不是", 1 - noul.Probability, noul.Probability < 0.5),
                    });

            case ChoiceAnswer choice:
            {
                var choiceQuestion = question as ChoiceQuestion;
                var rows = new List<JudgmentRow>(choice.Probabilities.Count);
                foreach (var probability in choice.Probabilities)
                {
                    var label = choiceQuestion?.LabelFor(probability.Key) ?? probability.Key;
                    rows.Add(new JudgmentRow(
                        label,
                        probability.Probability,
                        string.Equals(probability.Key, choice.Choice, StringComparison.Ordinal)));
                }

                return new JudgmentCard(question.Label, null, Trim(rows));
            }

            case ScoreAnswer score:
            {
                var rows = new List<JudgmentRow>(score.Legend.Count);
                var hasDistribution = score.Probabilities.Count > 0;

                // Without a distribution, every bar would draw at 0% — which reads as a
                // confident "none of these" rather than as a missing value. Fall back to
                // marking the level the score lands on and drawing no bars.
                var top = hasDistribution
                    ? IndexOfMax(score.Probabilities)
                    : Math.Clamp((int)Math.Round(score.Score), 0, Math.Max(0, score.Legend.Count - 1));

                for (var i = 0; i < score.Legend.Count; i++)
                {
                    var probability = hasDistribution && i < score.Probabilities.Count
                        ? score.Probabilities[i]
                        : 0;
                    rows.Add(new JudgmentRow(score.Legend[i], probability, i == top, hasDistribution));
                }

                var summary = $"{score.Score:N1} / {Math.Max(0, score.Legend.Count - 1)}";
                return new JudgmentCard(question.Label, summary, rows);
            }

            default:
                return new JudgmentCard(question.Label, "未知的答案类型", Array.Empty<JudgmentRow>());
        }
    }

    /// <summary>
    /// Drops the long tail of near-zero options a choice question always carries.
    /// Rows arrive sorted, so this keeps the head; it never reorders and never hides
    /// an option that still holds a meaningful share.
    /// </summary>
    private static IReadOnlyList<JudgmentRow> Trim(IReadOnlyList<JudgmentRow> rows)
    {
        const double floor = 0.01;
        const int minimum = 2;
        const int maximum = 5;

        var kept = new List<JudgmentRow>(Math.Min(rows.Count, maximum));
        foreach (var row in rows)
        {
            if (kept.Count >= maximum)
            {
                break;
            }

            if (row.Probability >= floor || kept.Count < minimum)
            {
                kept.Add(row);
            }
        }

        return kept;
    }

    private static int IndexOfMax(IReadOnlyList<double> values)
    {
        var best = 0;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }

        return best;
    }
}
