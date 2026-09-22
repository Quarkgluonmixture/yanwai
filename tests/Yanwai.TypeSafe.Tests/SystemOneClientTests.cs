using System.Text.Json;
using Yanwai.TypeSafe;
using Xunit;

namespace Yanwai.TypeSafe.Tests;

public sealed class SystemOneRequestTests
{
    private static readonly ChoiceQuestion Choice = new(
        "speech_act",
        "这句话在做什么",
        "这条消息在做什么？",
        new ChoiceOption[]
        {
            new("ask_info", "在问信息", "在索取信息"),
            new("tease", "在打趣", "在开玩笑"),
        });

    private static readonly ScoreQuestion Score = new(
        "tension",
        "对话张力",
        "紧张程度。",
        new[] { "轻松", "正常", "有情绪" });

    [Fact]
    public void ChoiceCriteriaIsAnObjectKeyedByOption()
    {
        var json = SystemOneClient.BuildRequest("state", new JevQuestion[] { Choice }, "jev-latest");

        using var document = JsonDocument.Parse(json);
        var question = document.RootElement.GetProperty("questions").GetProperty("speech_act");

        Assert.Equal("choice", question.GetProperty("type").GetString());
        var criteria = question.GetProperty("criteria");
        Assert.Equal(JsonValueKind.Object, criteria.ValueKind);
        Assert.Equal("在索取信息", criteria.GetProperty("ask_info").GetString());
        Assert.Equal("在开玩笑", criteria.GetProperty("tease").GetString());
    }

    [Fact]
    public void ScoreCriteriaIsAnOrderedArray()
    {
        var json = SystemOneClient.BuildRequest("state", new JevQuestion[] { Score }, "jev-latest");

        using var document = JsonDocument.Parse(json);
        var criteria = document.RootElement
            .GetProperty("questions").GetProperty("tension").GetProperty("criteria");

        Assert.Equal(JsonValueKind.Array, criteria.ValueKind);
        Assert.Equal(new[] { "轻松", "正常", "有情绪" }, criteria.EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void NoulQuestionCarriesNoCriteria()
    {
        var noul = new NoulQuestion("literal", "字面 = 本意", "字面就是全部吗？");

        var json = SystemOneClient.BuildRequest("state", new JevQuestion[] { noul }, "jev-latest");

        using var document = JsonDocument.Parse(json);
        var question = document.RootElement.GetProperty("questions").GetProperty("literal");
        Assert.Equal("noul", question.GetProperty("type").GetString());
        Assert.False(question.TryGetProperty("criteria", out _));
    }

    [Fact]
    public void ScoreLegendIsOrderedByIndexNotByPropertyOrder()
    {
        // The server may serialise the legend in any property order. Ordering by the
        // stringified index is what keeps level 0 at the bottom of the card.
        const string body = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "tension": {
              "type": "score",
              "score": 1.4,
              "legend": { "2": "有情绪", "0": "轻松", "1": "正常" },
              "probabilities": [0.2, 0.5, 0.3]
            }
          }
        }
        """;

        var result = SystemOneClient.ParseResponse(body, new JevQuestion[] { Score }, TimeSpan.Zero);

        var answer = Assert.IsType<ScoreAnswer>(result.Answers["tension"]);
        Assert.Equal(new[] { "轻松", "正常", "有情绪" }, answer.Legend);
        Assert.Equal("正常", answer.NearestLevel);
    }

    [Fact]
    public void ScoreProbabilitiesArriveKeyedByLevelIndex()
    {
        // Observed shape from api.typesafe.ai on 2026-09-22. The prose docs show an
        // array; the service sends an object. Reading only the array shape yielded an
        // empty distribution that rendered as every level at 0%.
        const string body = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "tension": {
              "type": "score",
              "score": 2.22,
              "confidence": 0.74,
              "legend": { "0": "轻松", "1": "正常", "2": "有情绪" },
              "probabilities": { "0": 0.0, "1": 0.02, "2": 0.98 }
            }
          }
        }
        """;

        var result = SystemOneClient.ParseResponse(body, new JevQuestion[] { Score }, TimeSpan.Zero);

        var answer = Assert.IsType<ScoreAnswer>(result.Answers["tension"]);
        Assert.Equal(new[] { 0.0, 0.02, 0.98 }, answer.Probabilities);
        Assert.Equal(0.74, answer.Confidence);
    }

    [Fact]
    public void AMissingScoreDistributionStaysEmptyRatherThanAllZero()
    {
        // Empty and "all levels at 0" must stay distinguishable: the view draws them
        // differently, and an all-zero distribution would read as a real verdict.
        const string body = """
        {"answers": {"tension": {"type": "score", "score": 1.0, "legend": {"0":"轻松","1":"正常","2":"有情绪"}}}}
        """;

        var result = SystemOneClient.ParseResponse(body, new JevQuestion[] { Score }, TimeSpan.Zero);

        var answer = Assert.IsType<ScoreAnswer>(result.Answers["tension"]);
        Assert.Empty(answer.Probabilities);
        Assert.Equal(3, answer.Legend.Count);
    }

    [Fact]
    public void ScoreLegendFallsBackToTheLevelsWeSent()
    {
        const string body = """
        {"answers": {"tension": {"type": "score", "score": 0.0, "probabilities": [1, 0, 0]}}}
        """;

        var result = SystemOneClient.ParseResponse(body, new JevQuestion[] { Score }, TimeSpan.Zero);

        var answer = Assert.IsType<ScoreAnswer>(result.Answers["tension"]);
        Assert.Equal(new[] { "轻松", "正常", "有情绪" }, answer.Legend);
    }

    [Fact]
    public void ChoiceProbabilitiesComeBackSortedDescending()
    {
        const string body = """
        {
          "answers": {
            "speech_act": {
              "type": "choice",
              "choice": "tease",
              "probabilities": { "ask_info": 0.18, "tease": 0.82 },
              "confidence": 0.66
            }
          }
        }
        """;

        var result = SystemOneClient.ParseResponse(body, new JevQuestion[] { Choice }, TimeSpan.Zero);

        var answer = Assert.IsType<ChoiceAnswer>(result.Answers["speech_act"]);
        Assert.Equal(new[] { "tease", "ask_info" }, answer.Probabilities.Select(p => p.Key));
        Assert.Equal("tease", answer.Choice);
        Assert.Equal(0.66, answer.Confidence);
    }

    [Fact]
    public void AMissingNoulValueThrowsInsteadOfReportingZero()
    {
        // A silently-zero probability would render as a confident "不是 100%".
        const string body = """{"answers": {"literal": {"type": "noul"}}}""";
        var noul = new NoulQuestion("literal", "字面 = 本意", "字面就是全部吗？");

        var exception = Assert.Throws<JevException>(
            () => SystemOneClient.ParseResponse(body, new JevQuestion[] { noul }, TimeSpan.Zero));

        Assert.Contains("noul", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnansweredQuestionIsAbsentRatherThanDefaulted()
    {
        const string body = """{"answers": {}}""";

        var result = SystemOneClient.ParseResponse(body, new JevQuestion[] { Choice, Score }, TimeSpan.Zero);

        Assert.Empty(result.Answers);
    }

    [Fact]
    public void AnEmptyApiKeyIsRejectedLikeAMissingOne()
    {
        Assert.Throws<JevException>(() => new SystemOneClient(string.Empty));
        Assert.Throws<JevException>(() => new SystemOneClient("   "));
    }
}
