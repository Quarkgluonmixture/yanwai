namespace Yanwai.TypeSafe;

public enum TranscriptSpeaker
{
    Remote,
    Self,
    Other,
}

/// <summary>
/// One observed message, reduced to what a transcript needs.
/// <paramref name="IsTextless"/> marks a bubble OCR read as carrying no text at all —
/// a sticker, image, emoji or voice note. That is a real turn and belongs in the
/// context; it is not the same as text OCR failed to read, which does not.
/// </summary>
public sealed record TranscriptTurn(
    string Id,
    TranscriptSpeaker Speaker,
    string Text,
    bool IsTrusted,
    bool IsTextless = false);

public sealed record TranscriptBuildResult(string Transcript, int SkippedUntrusted);

/// <summary>
/// Turns observed messages into the "对方:" / "我:" lines that <see cref="ConversationInput"/>
/// parses. Kept free of the observer types so it can be tested without a capture.
/// </summary>
public static class TranscriptBuilder
{
    /// <summary>How a bubble with no readable text is written into the context.</summary>
    public const string TextlessPlaceholder = "[表情或图片]";


    /// <summary>
    /// Builds the context ending at <paramref name="targetId"/>. Turns after the target
    /// are dropped: from the judgment's point of view they have not happened yet, and
    /// including a reply would leak the answer into its own question.
    ///
    /// Returns null when nothing usable is left — never a partial transcript that
    /// silently omits the message being judged.
    /// </summary>
    public static TranscriptBuildResult? Build(
        IReadOnlyList<TranscriptTurn> turns,
        string targetId,
        int maxTurns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTurns, 1);

        var targetIndex = -1;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (string.Equals(turns[i].Id, targetId, StringComparison.Ordinal))
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex < 0)
        {
            return null;
        }

        // A textless target is a real message but there is nothing to judge, and text
        // OCR could not read must not be judged. Both stop here; the caller decides how
        // to say so.
        if (!turns[targetIndex].IsTrusted || turns[targetIndex].IsTextless)
        {
            return null;
        }

        var first = Math.Max(0, targetIndex - maxTurns + 1);
        var lines = new List<string>(targetIndex - first + 1);
        var skipped = 0;

        for (var i = first; i <= targetIndex; i++)
        {
            var turn = turns[i];
            var textless = turn.IsTextless;
            if (!textless && (!turn.IsTrusted || string.IsNullOrWhiteSpace(turn.Text)))
            {
                skipped++;
                continue;
            }

            var speaker = turn.Speaker switch
            {
                TranscriptSpeaker.Remote => "对方",
                TranscriptSpeaker.Self => "我",
                _ => null,
            };

            if (speaker is null)
            {
                skipped++;
                continue;
            }

            lines.Add($"{speaker}: {(textless ? TextlessPlaceholder : turn.Text)}");
        }

        return lines.Count == 0 ? null : new TranscriptBuildResult(string.Join("\n", lines), skipped);
    }
}
