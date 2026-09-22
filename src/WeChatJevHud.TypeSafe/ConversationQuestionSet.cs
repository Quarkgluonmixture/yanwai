namespace WeChatJevHud.TypeSafe;

/// <summary>
/// The questions asked about one incoming message, read against the recent
/// conversation as shared state.
///
/// Shape rules, from docs/DECISIONS.md D-002 and from the failure modes an earlier
/// third-party attempt documented (github.com/duckegg0623-create/jev-wechat-live,
/// KNOWN_ISSUES.md):
///
/// - several narrow questions, not one general "what did they mean" classifier;
/// - every choice carries an explicit escape option, because a forced pick reports a
///   fallback with the same confidence shape as a decision;
/// - the wording a person reads is the wording Jev is asked. Abstract taxonomy labels
///   ("在确认收到", "被听见") are accurate and unreadable; plain phrasing is both.
/// </summary>
public static class ConversationQuestionSet
{
    public const string EscapeOptionKey = "unclear";

    /// <summary>Where the message is quoted back inside a question heading.</summary>
    private const int QuoteLength = 14;

    /// <summary>The question whose top option is worth showing as the headline advice.</summary>
    public const string ReplyKey = "reply";

    /// <summary>The question worth showing as the headline severity.</summary>
    public const string DangerKey = "danger";

    public static IReadOnlyList<JevQuestion> For(string targetMessage)
    {
        var quote = Quote(targetMessage);

        return new JevQuestion[]
        {
            new NoulQuestion(
                "subtext",
                $"「{quote}」是话里有话吗？",
                "这句话除了字面意思，还想表达别的吗？如果字面说的就是全部，答否。"),

            new ChoiceQuestion(
                "mood",
                "她现在什么状态",
                "从她的用词和语气看，她此刻的状态更像哪一种？",
                new ChoiceOption[]
                {
                    new("calm", "平静，随口说的", "语气平常，没有情绪起伏"),
                    new("impatient", "有点不耐烦", "在催，或者觉得你反应慢"),
                    new("angry", "明显在生气", "带着火气，措辞变冲"),
                    new("hurt", "有点委屈、失落", "情绪往下走，像是被冷落或失望"),
                    new("joking", "在开玩笑", "在逗你、调侃，不是真的在说事"),
                    new("happy", "挺高兴", "语气轻快、正面"),
                    new(EscapeOptionKey, "看不出来", "线索不够，判断不了"),
                }),

            new ChoiceQuestion(
                "wants",
                "她要的是什么",
                "她发这句话，最想从你这里得到什么？",
                new ChoiceOption[]
                {
                    new("answer", "要一个具体答案", "在等你回答一个具体的问题"),
                    new("do_something", "要你去做件事", "在等你采取一个实际行动"),
                    new("comfort", "要你哄两句", "在等你接住她的情绪"),
                    new("stance", "要你表个态", "在等你给一个态度或承诺"),
                    new("nothing", "没想要什么", "只是说一句，不需要你做什么"),
                    new(EscapeOptionKey, "看不出来", "线索不够，判断不了"),
                }),

            new ChoiceQuestion(
                ReplyKey,
                "现在该怎么回",
                "接下来你最该做的一步是什么？",
                new ChoiceOption[]
                {
                    new("answer_now", "直接回答", "她要的就是答案，给她"),
                    new("check_history", "先翻聊天记录", "她指的是之前说过的事，先确认是什么"),
                    new("make_a_plan", "给个具体安排", "别再表态了，给出时间地点或具体做法"),
                    new("handle_feeling", "先接住情绪", "先回应她的感受，再谈事情"),
                    new("ask_what", "问清楚她指什么", "不问清楚就回，容易回错"),
                    new("stop_explaining", "别再解释了", "已经说够了，再补一句反而更糟"),
                    new("no_reply", "不用回", "这条不需要回复"),
                    new(EscapeOptionKey, "说不好", "以上都不合适"),
                }),

            new ScoreQuestion(
                DangerKey,
                "危险等级",
                "如果接下来这一句回得不好，这段对话会往坏处走的程度。",
                new[]
                {
                    "没事，随便聊",
                    "留点心，她在意这件事",
                    "要认真回，已经有情绪了",
                    "很危险，回不好就吵起来",
                }),
        };
    }

    private static string Quote(string message)
    {
        var text = message.Trim();
        if (text.Length == 0)
        {
            return "这句话";
        }

        return text.Length <= QuoteLength ? text : text[..QuoteLength] + "…";
    }
}
