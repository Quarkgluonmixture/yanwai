using Yanwai.Overlay;
using Yanwai.TypeSafe;

namespace Yanwai.Panel;

/// <summary>
/// One message and what Jev said about it. Built once per Jev call and kept, so
/// selecting it again or scrolling back to it costs nothing.
/// </summary>
public sealed class JudgedMessage
{
    private JudgedMessage(
        string id,
        string text,
        IReadOnlyList<JevQuestion> questions,
        JevResult result,
        int skippedUntrusted,
        string? danger,
        int? dangerLevel,
        string? advice,
        double adviceProbability)
    {
        Id = id;
        Text = text;
        Questions = questions;
        Result = result;
        SkippedUntrusted = skippedUntrusted;
        Danger = danger;
        DangerLevel = dangerLevel;
        Advice = advice;
        AdviceProbability = adviceProbability;
    }

    public string Id { get; }

    public string Text { get; }

    public IReadOnlyList<JevQuestion> Questions { get; }

    public JevResult Result { get; }

    public int SkippedUntrusted { get; }

    /// <summary>The danger level's wording, or null when that answer did not come back.</summary>
    public string? Danger { get; }

    public int? DangerLevel { get; }

    /// <summary>The top reply option's wording, or null when that answer did not come back.</summary>
    public string? Advice { get; }

    public double AdviceProbability { get; }

    public bool HasHeadline => Danger is not null && Advice is not null;

    public string AdviceText => Advice is null ? "—" : $"{Advice}  {AdviceProbability * 100:N0}%";

    /// <summary>What the panel's list shows for this message.</summary>
    public string ListLabel => $"{Text}\n{(HasHeadline ? $"{Danger} · {AdviceText}" : "判定不完整")}";

    public static JudgedMessage From(
        string id,
        string text,
        IReadOnlyList<JevQuestion> questions,
        JevResult result,
        int skippedUntrusted)
    {
        string? danger = null;
        int? dangerLevel = null;
        if (result.Answers.TryGetValue(ConversationQuestionSet.DangerKey, out var dangerAnswer)
            && dangerAnswer is ScoreAnswer score
            && score.NearestLevel is { } level)
        {
            danger = level;
            dangerLevel = Math.Clamp((int)Math.Round(score.Score), 0, score.Legend.Count - 1);
        }

        string? advice = null;
        double adviceProbability = 0;
        if (result.Answers.TryGetValue(ConversationQuestionSet.ReplyKey, out var replyAnswer)
            && replyAnswer is ChoiceAnswer { Probabilities.Count: > 0 } reply)
        {
            var question = questions.FirstOrDefault(q => q.Key == ConversationQuestionSet.ReplyKey) as ChoiceQuestion;
            var top = reply.Probabilities[0];
            advice = question?.LabelFor(top.Key) ?? top.Key;
            adviceProbability = top.Probability;
        }

        return new JudgedMessage(
            id, text, questions, result, skippedUntrusted, danger, dangerLevel, advice, adviceProbability);
    }

    public IReadOnlyList<JudgmentCard> Cards()
    {
        var cards = new List<JudgmentCard>();
        foreach (var question in Questions)
        {
            if (Result.Answers.TryGetValue(question.Key, out var answer))
            {
                cards.Add(JudgmentCard.FromAnswer(question, answer));
            }
        }

        return cards;
    }

    public IReadOnlyList<string> MissingKeys() =>
        Questions.Where(q => !Result.Answers.ContainsKey(q.Key)).Select(q => q.Key).ToList();

    public OverlayChip Chip() =>
        new(HasHeadline ? AdviceText : "判定不完整", DangerLevel);

    public OverlayContent OverlayCard()
    {
        var rows = new List<OverlayRow>(2);
        foreach (var key in new[] { "subtext", "wants" })
        {
            if (!Result.Answers.TryGetValue(key, out var answer))
            {
                continue;
            }

            switch (answer)
            {
                case NoulAnswer noul:
                    var question = Questions.FirstOrDefault(q => q.Key == key);
                    rows.Add(new OverlayRow(question?.Label ?? key, noul.Probability));
                    break;

                case ChoiceAnswer { Probabilities.Count: > 0 } choice:
                    var choiceQuestion = Questions.FirstOrDefault(q => q.Key == key) as ChoiceQuestion;
                    var best = choice.Probabilities[0];
                    rows.Add(new OverlayRow(choiceQuestion?.LabelFor(best.Key) ?? best.Key, best.Probability));
                    break;
            }
        }

        return new OverlayContent(Text, Danger ?? "—", AdviceText, rows);
    }
}
