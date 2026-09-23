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
        bool textVerified,
        int unverifiedInContext,
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
        TextVerified = textVerified;
        UnverifiedInContext = unverifiedInContext;
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

    /// <summary>False when the OCR cross-check could not confirm the text that was judged.</summary>
    public bool TextVerified { get; }

    public int UnverifiedInContext { get; }

    /// <summary>The danger level's wording, or null when that answer did not come back.</summary>
    public string? Danger { get; }

    public int? DangerLevel { get; }

    /// <summary>The top reply option's wording, or null when that answer did not come back.</summary>
    public string? Advice { get; }

    public double AdviceProbability { get; }

    public bool HasHeadline => Danger is not null && Advice is not null;

    public string AdviceText => Advice is null ? "—" : $"{Advice}  {AdviceProbability * 100:N0}%";

    /// <summary>What the panel's list shows for this message.</summary>
    public string ListLabel =>
        $"{(TextVerified ? "" : "[OCR 未核实] ")}{Text}\n{(HasHeadline ? $"{Danger} · {AdviceText}" : "判定不完整")}";

    public static JudgedMessage From(
        string id,
        string text,
        IReadOnlyList<JevQuestion> questions,
        JevResult result,
        int skippedUntrusted,
        bool textVerified = true,
        int unverifiedInContext = 0)
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
            id, text, questions, result, skippedUntrusted, textVerified, unverifiedInContext,
            danger, dangerLevel, advice, adviceProbability);
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

    /// <summary>Choices show this many options; the rest are in the panel.</summary>
    private const int CardOptions = 3;

    private static readonly HashSet<string> CardKeys =
        ["subtext", ConversationQuestionSet.ReplyKey, ConversationQuestionSet.DangerKey];

    /// <summary>
    /// The card under the bubble: the yes/no read on subtext, what to do next, and the
    /// danger level — three sections, so it stays short enough to fit between messages.
    /// Mood and wants are in the panel. Questions that came back unanswered are left
    /// out, not filled in.
    /// </summary>
    public OverlayContent OverlayCard()
    {
        var sections = new List<OverlaySection>();
        if (!TextVerified)
        {
            // The judgment is about this reading; if it is wrong, the user must be able to see that.
            sections.Add(new OverlaySection($"OCR 未核实，判的是：「{Text}」", []));
        }

        foreach (var question in Questions)
        {
            if (!CardKeys.Contains(question.Key) || !Result.Answers.TryGetValue(question.Key, out var answer))
            {
                continue;
            }

            switch (question, answer)
            {
                case (NoulQuestion, NoulAnswer noul):
                    sections.Add(new OverlaySection(
                        question.Label,
                        [new OverlayRow("是", noul.Probability), new OverlayRow("不是", 1 - noul.Probability)]));
                    break;

                case (ChoiceQuestion choiceQuestion, ChoiceAnswer { Probabilities.Count: > 0 } choice):
                    sections.Add(new OverlaySection(
                        choiceQuestion.Label,
                        choice.Probabilities
                            .Take(CardOptions)
                            .Select(option => new OverlayRow(choiceQuestion.LabelFor(option.Key), option.Probability))
                            .ToList()));
                    break;

                case (ScoreQuestion, ScoreAnswer score) when score.Legend.Count > 0:
                    sections.Add(new OverlaySection(
                        $"{question.Label}：{score.Score:0.0} / {score.Legend.Count - 1}（{score.NearestLevel}）",
                        []));
                    break;
            }
        }

        return new OverlayContent(sections);
    }
}
