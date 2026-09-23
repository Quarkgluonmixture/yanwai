namespace Yanwai.Ocr.Tests;

public sealed class CrossCheckedOcrEngineTests
{
    private static readonly ImageCrop AnyCrop = new(
        new Yanwai.Capture.CapturedFrame(
            1, 1, 4, new byte[4], default, Yanwai.Capture.CaptureMethod.RenderWindow, DateTimeOffset.UnixEpoch, TimeSpan.Zero),
        new Yanwai.Core.Geometry.CapturePixelRect(0, 0, 1, 1));

    private static Task<OcrResult> Run(string primary, string checker, string unemittable = "")
    {
        var engine = new CrossCheckedOcrEngine(
            new FixedEngine(primary, 0.99, OcrTextStatus.LowConfidence),
            new FixedEngine(checker, null, OcrTextStatus.Recognized),
            character => !unemittable.Contains(character));
        return engine.RecognizeAsync(AnyCrop, CancellationToken.None);
    }

    [Fact]
    public async Task Agreement_up_to_width_and_spacing_is_trusted()
    {
        var result = await Run("在坡她就说：好的 ok", "在坡她就说:好的OK");

        Assert.Equal(OcrTextStatus.Recognized, result.Status);
        Assert.Equal("在坡她就说：好的 ok", result.Text);
    }

    /// <summary>A confident primary is still not trusted when the checker reads something else.</summary>
    [Fact]
    public async Task A_different_line_is_never_trusted_however_confident_the_primary_is()
    {
        var result = await Run("你最好是", "我不知道");

        Assert.Equal(OcrTextStatus.LowConfidence, result.Status);
    }

    /// <summary>The weaker checker misreading one glyph must not throw away a correct reading.</summary>
    [Fact]
    public async Task A_one_character_slip_in_the_checker_keeps_the_primary_reading()
    {
        var result = await Run("一直异地谈不了的sos", "一直异地谈不了的s。s");

        Assert.Equal(OcrTextStatus.Recognized, result.Status);
        Assert.Equal("一直异地谈不了的sos", result.Text);
    }

    [Fact]
    public async Task Two_slips_in_a_short_line_are_too_many()
    {
        var result = await Run("怎么说呢", "怎久讲呢");

        Assert.Equal(OcrTextStatus.LowConfidence, result.Status);
    }

    /// <summary>PP-OCR has no 诶: it drops it and reports full confidence. The checker's reading wins.</summary>
    [Fact]
    public async Task Characters_the_primary_cannot_produce_are_taken_from_the_checker()
    {
        var result = await Run("哟我去", "诶哟我去", unemittable: "诶");

        Assert.Equal(OcrTextStatus.Recognized, result.Status);
        Assert.Equal("诶哟我去", result.Text);
    }

    [Fact]
    public async Task An_extra_character_the_primary_could_have_produced_is_not_adopted()
    {
        var result = await Run("哟我去", "嗯哟我去", unemittable: "诶");

        // Tolerated as a slip, but the primary's reading is kept: 嗯 is not taken on trust.
        Assert.Equal("哟我去", result.Text);
    }

    private sealed class FixedEngine(string text, double? confidence, OcrTextStatus status) : IOcrEngine
    {
        public string Name => "fixed";

        public Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken) =>
            Task.FromResult(text.Length == 0
                ? new OcrResult(string.Empty, null, OcrTextStatus.NoText, string.Empty)
                : new OcrResult(text, confidence, status, text));
    }
}
