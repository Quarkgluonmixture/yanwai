namespace WeChatJevHud.TypeSafe;

/// <summary>
/// The default question set for a single incoming message, read against the recent
/// conversation as shared state.
///
/// Shape rules this set follows, from docs/DECISIONS.md D-002 and from the failure
/// modes documented by an earlier third-party attempt at the same product
/// (github.com/duckegg0623-create/jev-wechat-live, KNOWN_ISSUES.md):
///
/// - many narrow questions, not one general "what did they mean" classifier;
/// - every choice question carries an explicit "none of these" option, because a
///   forced pick reports a fallback with the same confidence shape as a decision;
/// - questions ask about observable conversational behavior, not about the other
///   person's character or inner state (AGENTS.md).
/// </summary>
public static class ConversationQuestionSet
{
    public const string NoneOfTheseKey = "none_of_these";

    public static IReadOnlyList<JevQuestion> Default { get; } = new JevQuestion[]
    {
        new NoulQuestion(
            "literal",
            "字面 = 本意",
            "这条消息的字面意思，就是对方想表达的全部内容吗？（如果话里有话、有言外之意，答否）"),

        new NoulQuestion(
            "expects_reply",
            "期待回应",
            "这条消息是否明确期待对方现在就给出回应？"),

        new NoulQuestion(
            "refers_prior",
            "指向前文",
            "要理解这条消息，是否必须依赖之前的对话内容？"),

        new ChoiceQuestion(
            "speech_act",
            "这句话在做什么",
            "这条消息在对话中执行的动作是哪一种？只依据可观察的说话方式判断。",
            new ChoiceOption[]
            {
                new("ask_info", "在问信息", "在索取一项具体的信息或事实"),
                new("request_action", "在要求行动", "在要求对方去做某件事"),
                new("defend_self", "在自辩", "在为自己的行为辩解或澄清误解"),
                new("express_dissatisfaction", "在表达不满", "在表达不满、抱怨或责备"),
                new("tease", "在打趣", "在开玩笑、调侃或玩梗，不带真实敌意"),
                new("share", "在分享告知", "在分享一件事或告知一个情况"),
                new("acknowledge", "在确认收到", "在应答、确认或表示知道了"),
                new("press", "在催促", "在追问或催促一个尚未得到的答复"),
                new(NoneOfTheseKey, "以上都不是", "以上选项都不贴切"),
            }),

        new ChoiceQuestion(
            "wants_next",
            "对方想要什么",
            "根据这条消息，对方接下来最希望得到什么？",
            new ChoiceOption[]
            {
                new("answer", "一个具体答案", "一项具体的信息或回答"),
                new("commitment", "一个行动承诺", "一个明确的、由说话人负责执行的行动安排"),
                new("apology", "一句道歉", "对某件事的致歉"),
                new("explanation", "一个解释", "对原因或经过的说明"),
                new("acknowledgement", "被听见", "被认真听到并接住情绪，不需要解决方案"),
                new("nothing", "不需要回应", "不需要对方做什么"),
                new(NoneOfTheseKey, "以上都不是", "以上选项都不贴切"),
            }),

        new ScoreQuestion(
            "tension",
            "对话张力",
            "从可观察的用词和语气看，这段对话此刻的紧张程度。",
            new[]
            {
                "轻松，在闲聊或开玩笑",
                "正常，平实地在交流",
                "有明显情绪，需要认真对待",
                "冲突中，措辞已经带攻击性",
            }),

        new ChoiceQuestion(
            "best_action",
            "接下来怎么办",
            "对方刚发来这条消息，说话人接下来最应该做的一步是什么？",
            new ChoiceOption[]
            {
                new("answer_directly", "直接回答", "直接给出对方要的答案"),
                new("check_first", "先查证再答", "先去确认事实或翻查记录，再回答"),
                new("commit_action", "给出具体行动", "提出一个具体的、由自己负责的行动安排"),
                new("acknowledge_feeling", "先接住情绪", "先回应对方的情绪，再谈事情"),
                new("ask_clarify", "追问澄清", "先问清楚对方的意思"),
                new("stop_talking", "不必再补充", "已经说够了，继续解释反而更糟"),
                new("no_reply_needed", "不需要回应", "这条不需要回复"),
                new(NoneOfTheseKey, "以上都不是", "以上选项都不贴切"),
            }),
    };
}
