namespace Yanwai.TypeSafe;

/// <summary>
/// A pasted conversation. Lines are expected as "对方: ..." / "我: ..."; an
/// unprefixed line is attributed to whoever spoke last, so a wrapped message does
/// not silently become a new turn.
/// </summary>
public sealed record ConversationInput(string State, string TargetMessage)
{
    private static readonly string[] RemotePrefixes = { "对方:", "对方：", "TA:", "TA：", "她:", "她：", "他:", "他：" };
    private static readonly string[] SelfPrefixes = { "我:", "我：" };

    public static bool TryParse(string raw, out ConversationInput input, out string error)
    {
        input = new ConversationInput(string.Empty, string.Empty);
        error = string.Empty;

        var lines = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ', '\t');
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }

        if (lines.Count == 0)
        {
            error = "先粘一段对话进来。";
            return false;
        }

        var lastRemote = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (HasPrefix(lines[i], RemotePrefixes))
            {
                lastRemote = i;
                break;
            }
        }

        if (lastRemote < 0)
        {
            error = "没找到对方发的消息。每行前面加「对方:」或「我:」。";
            return false;
        }

        var target = StripPrefix(lines[lastRemote], RemotePrefixes);
        if (target.Length == 0)
        {
            error = "对方最后那条是空的。";
            return false;
        }

        var state = BuildState(lines, lastRemote);
        input = new ConversationInput(state, target);
        return true;
    }

    private static string BuildState(IReadOnlyList<string> lines, int targetIndex)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("这是一段微信对话。「对方」是聊天的另一方，「我」是使用这个工具的人。");
        builder.AppendLine("以下是按时间顺序的完整对话记录：");
        builder.AppendLine();

        for (var i = 0; i <= targetIndex; i++)
        {
            builder.AppendLine(Normalize(lines[i]));
        }

        builder.AppendLine();
        builder.Append("需要判断的是对方发来的最后一条：");
        builder.Append(StripPrefix(lines[targetIndex], RemotePrefixes));
        return builder.ToString();
    }

    private static string Normalize(string line)
    {
        if (HasPrefix(line, RemotePrefixes))
        {
            return "对方: " + StripPrefix(line, RemotePrefixes);
        }

        if (HasPrefix(line, SelfPrefixes))
        {
            return "我: " + StripPrefix(line, SelfPrefixes);
        }

        return line;
    }

    private static bool HasPrefix(string line, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripPrefix(string line, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return line;
    }
}
