using Yanwai.TypeSafe;
using Xunit;

namespace Yanwai.TypeSafe.Tests;

public sealed class TranscriptBuilderTests
{
    private static TranscriptTurn Remote(string id, string text, bool trusted = true) =>
        new(id, TranscriptSpeaker.Remote, text, trusted);

    private static TranscriptTurn Self(string id, string text, bool trusted = true) =>
        new(id, TranscriptSpeaker.Self, text, trusted);

    /// <summary>
    /// Unverified OCR is usable text: dropping it emptied most of a real screen. It stays
    /// in the context and is counted, so the panel can say how much of it was unverified.
    /// </summary>
    [Fact]
    public void UnverifiedTurnsStayInTheContextAndAreCounted()
    {
        var turns = new[]
        {
            new TranscriptTurn("m1", TranscriptSpeaker.Self, "等一下", IsTrusted: true, IsVerified: false),
            new TranscriptTurn("m2", TranscriptSpeaker.Remote, "你最好是", IsTrusted: true, IsVerified: false),
        };

        var result = TranscriptBuilder.Build(turns, "m2", 12);

        Assert.NotNull(result);
        Assert.Equal("我: 等一下\n对方: 你最好是", result!.Transcript);
        Assert.Equal(2, result.UnverifiedCount);
        Assert.Equal(0, result.SkippedUntrusted);
    }

    [Fact]
    public void TurnsAfterTheTargetAreDropped()
    {
        // The reply is already on screen by the time OCR catches up. Feeding it back in
        // would let the judgment read the answer to its own question.
        var turns = new[]
        {
            Remote("m1", "所以呢？"),
            Self("m2", "所以这次我来安排。"),
        };

        var result = TranscriptBuilder.Build(turns, "m1", 12);

        Assert.NotNull(result);
        Assert.Equal("对方: 所以呢？", result!.Transcript);
    }

    [Fact]
    public void EarlierTurnsAreKeptInOrderWithSpeakerPrefixes()
    {
        var turns = new[]
        {
            Remote("m1", "第一句"),
            Self("m2", "第二句"),
            Remote("m3", "第三句"),
        };

        var result = TranscriptBuilder.Build(turns, "m3", 12);

        Assert.NotNull(result);
        Assert.Equal("对方: 第一句\n我: 第二句\n对方: 第三句", result!.Transcript);
        Assert.Equal(0, result.SkippedUntrusted);
    }

    [Fact]
    public void UntrustedContextIsSkippedAndCounted()
    {
        var turns = new[]
        {
            Remote("m1", "看得清"),
            Self("m2", "糊了", trusted: false),
            Remote("m3", "目标"),
        };

        var result = TranscriptBuilder.Build(turns, "m3", 12);

        Assert.NotNull(result);
        Assert.DoesNotContain("糊了", result!.Transcript, StringComparison.Ordinal);
        Assert.Equal(1, result.SkippedUntrusted);
    }

    [Fact]
    public void AnUntrustedTargetProducesNothing()
    {
        // Judging a message OCR could not read would put a confident panel next to a
        // guess. Returning null keeps the previous result on screen instead.
        var turns = new[] { Remote("m1", "???", trusted: false) };

        Assert.Null(TranscriptBuilder.Build(turns, "m1", 12));
    }

    [Fact]
    public void AnUnknownTargetProducesNothing()
    {
        var turns = new[] { Remote("m1", "在的") };

        Assert.Null(TranscriptBuilder.Build(turns, "missing", 12));
    }

    [Fact]
    public void TheWindowKeepsTheTurnsNearestTheTarget()
    {
        var turns = new[]
        {
            Remote("m1", "最早"),
            Self("m2", "中间"),
            Remote("m3", "目标"),
        };

        var result = TranscriptBuilder.Build(turns, "m3", 2);

        Assert.NotNull(result);
        Assert.Equal("我: 中间\n对方: 目标", result!.Transcript);
    }

    [Fact]
    public void SystemAndUnknownSidesNeverBecomeATurn()
    {
        var turns = new[]
        {
            new TranscriptTurn("m1", TranscriptSpeaker.Other, "—— 以上是打招呼内容 ——", true),
            Remote("m2", "在吗"),
        };

        var result = TranscriptBuilder.Build(turns, "m2", 12);

        Assert.NotNull(result);
        Assert.Equal("对方: 在吗", result!.Transcript);
        Assert.Equal(1, result.SkippedUntrusted);
    }

    [Fact]
    public void TheBuiltTranscriptParsesBackIntoConversationInput()
    {
        // The two halves must agree: whatever the watcher builds has to survive the
        // parser the manual path uses, or live mode silently analyses the wrong line.
        var turns = new[]
        {
            Remote("m1", "你今天是不是又忘了我跟你说过什么？"),
            Self("m2", "记得。"),
            Remote("m3", "那你说。"),
        };

        var result = TranscriptBuilder.Build(turns, "m3", 12);
        Assert.NotNull(result);

        Assert.True(ConversationInput.TryParse(result!.Transcript, out var input, out _));
        Assert.Equal("那你说。", input.TargetMessage);
    }
}
