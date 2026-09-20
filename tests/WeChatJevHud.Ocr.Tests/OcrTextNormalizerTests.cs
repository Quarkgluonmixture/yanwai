namespace WeChatJevHud.Ocr.Tests;

public sealed class OcrTextNormalizerTests
{
    [Theory]
    [InlineData("诶 哟 我 去", "诶哟我去")]
    [InlineData("中 文 mixed", "中文 mixed")]
    [InlineData("中 文 ， 测试", "中文，测试")]
    [InlineData("中文 ＡＢＣ", "中文 ＡＢＣ")]
    [InlineData("s 。 s", "s 。 s")]
    [InlineData("Hello OCR test 123, I just got home.", "Hello OCR test 123, I just got home.")]
    [InlineData("Hello,¦ I", "Hello, I")]
    [InlineData("first\r\nsecond", "first second")]
    [InlineData("或者上\\海才能 ¦", "或者上海才能")]
    [InlineData("但是她在宁波 |", "但是她在宁波")]
    public void Normalize_removes_ocr_spacing_artifacts_without_merging_english_words(
        string input,
        string expected)
    {
        Assert.Equal(expected, OcrTextNormalizer.Normalize(input));
    }
}
