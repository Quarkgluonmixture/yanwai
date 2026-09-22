using Yanwai.TypeSafe;
using Xunit;

namespace Yanwai.TypeSafe.Tests;

public sealed class ConversationInputTests
{
    [Fact]
    public void TheTargetIsTheLastRemoteLineNotTheLastLine()
    {
        const string raw = """
        对方: 那你说。
        我: 等一下，我想说完整一点。
        """;

        Assert.True(ConversationInput.TryParse(raw, out var input, out _));

        Assert.Equal("那你说。", input.TargetMessage);
    }

    [Fact]
    public void LinesAfterTheTargetAreNotSentAsContext()
    {
        // Anything the user typed after the remote message has not happened yet from
        // the judgment's point of view; including it would leak the answer.
        const string raw = """
        对方: 所以呢？
        我: 所以这次我来安排。
        """;

        Assert.True(ConversationInput.TryParse(raw, out var input, out _));

        Assert.DoesNotContain("所以这次我来安排", input.State, StringComparison.Ordinal);
        Assert.Contains("所以呢？", input.State, StringComparison.Ordinal);
    }

    [Fact]
    public void FullWidthAndAliasPrefixesAreAccepted()
    {
        Assert.True(ConversationInput.TryParse("她：你在干嘛", out var input, out _));

        Assert.Equal("你在干嘛", input.TargetMessage);
        Assert.Contains("对方: 你在干嘛", input.State, StringComparison.Ordinal);
    }

    [Fact]
    public void AConversationWithNoRemoteLineIsRejected()
    {
        Assert.False(ConversationInput.TryParse("我: 在的", out _, out var error));

        Assert.Contains("对方", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankInputIsRejected()
    {
        Assert.False(ConversationInput.TryParse("   \n\n  ", out _, out var error));

        Assert.NotEmpty(error);
    }

    [Fact]
    public void EarlierTurnsSurviveInOrder()
    {
        const string raw = """
        对方: 第一句
        我: 第二句
        对方: 第三句
        """;

        Assert.True(ConversationInput.TryParse(raw, out var input, out _));

        var first = input.State.IndexOf("第一句", StringComparison.Ordinal);
        var second = input.State.IndexOf("第二句", StringComparison.Ordinal);
        var third = input.State.IndexOf("第三句", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < second && second < third);
    }
}
