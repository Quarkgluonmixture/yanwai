using WeChatJevHud.TypeSafe;
using Xunit;

namespace WeChatJevHud.TypeSafe.Tests;

/// <summary>
/// A bubble OCR reads as carrying no text (sticker, image, emoji, voice note) is a real
/// turn. Dropping it entirely, as the first version did, left a gap in the context and
/// made a live sticker look like the tool had stopped working.
/// </summary>
public sealed class TextlessTurnTests
{
    private static TranscriptTurn Textless(string id, TranscriptSpeaker speaker) =>
        new(id, speaker, string.Empty, IsTrusted: false, IsTextless: true);

    [Fact]
    public void ATextlessTurnBecomesAPlaceholderInTheContext()
    {
        var turns = new[]
        {
            Textless("m1", TranscriptSpeaker.Remote),
            new TranscriptTurn("m2", TranscriptSpeaker.Remote, "在吗", true),
        };

        var result = TranscriptBuilder.Build(turns, "m2", 12);

        Assert.NotNull(result);
        Assert.Equal($"对方: {TranscriptBuilder.TextlessPlaceholder}\n对方: 在吗", result!.Transcript);
    }

    [Fact]
    public void ATextlessTurnIsNotCountedAsAnOcrFailure()
    {
        // "no text was there" and "text was there and we failed to read it" are
        // different facts; only the second is worth warning the user about.
        var turns = new[]
        {
            Textless("m1", TranscriptSpeaker.Self),
            new TranscriptTurn("m2", TranscriptSpeaker.Remote, "在吗", true),
        };

        var result = TranscriptBuilder.Build(turns, "m2", 12);

        Assert.NotNull(result);
        Assert.Equal(0, result!.SkippedUntrusted);
    }

    [Fact]
    public void ATextlessTargetIsNotJudged()
    {
        var turns = new[]
        {
            new TranscriptTurn("m1", TranscriptSpeaker.Remote, "在吗", true),
            Textless("m2", TranscriptSpeaker.Remote),
        };

        Assert.Null(TranscriptBuilder.Build(turns, "m2", 12));
    }

    [Fact]
    public void UnreadableTextIsStillExcludedFromTheContext()
    {
        // Distinct from textless: a low-confidence crop would put invented words into
        // the state the judgment reads.
        var turns = new[]
        {
            new TranscriptTurn("m1", TranscriptSpeaker.Remote, "锟斤拷", IsTrusted: false),
            new TranscriptTurn("m2", TranscriptSpeaker.Remote, "在吗", true),
        };

        var result = TranscriptBuilder.Build(turns, "m2", 12);

        Assert.NotNull(result);
        Assert.Equal("对方: 在吗", result!.Transcript);
        Assert.Equal(1, result.SkippedUntrusted);
    }
}
