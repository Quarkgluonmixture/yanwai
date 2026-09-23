namespace Yanwai.Ocr;

/// <summary>
/// Trusts text only when two independent engines agree on it.
///
/// The primary engine's own score is not enough: PP-OCR returned "哟我去" for "诶哟我去"
/// with confidence 1.00, because "诶" is not in its dictionary at all, and Phase 3
/// measured other wrong outputs above 0.90. A second engine with different failure
/// modes catches those.
///
/// - Both read the same text (after folding width, spacing and case) → Recognized.
/// - They differ only by characters the primary cannot produce → the checker's text is
///   the better reading → Recognized.
/// - They differ in at most <see cref="ToleratedDisagreement"/> of the characters (and at
///   least one is always tolerated) → the primary's reading, Recognized. The checker is
///   the weaker engine: on the public fixtures Windows OCR read 怎么说 as 怎久说 and sos
///   as s。s where PP-OCR was right, and strict equality threw those messages away.
///   This catches a wholly wrong line, not a single wrong character.
/// - Any larger disagreement → LowConfidence. The text stays visible for debugging but
///   is never trusted, per ACCEPTANCE Phase 3.
/// </summary>
public sealed class CrossCheckedOcrEngine : IOcrEngine
{
    private readonly IOcrEngine _primary;
    private readonly IOcrEngine _checker;
    private readonly Func<char, bool> _primaryCanEmit;

    /// <summary>Fraction of the primary's characters the two readings may differ in.</summary>
    public const double ToleratedDisagreement = 0.2;

    public CrossCheckedOcrEngine(IOcrEngine primary, IOcrEngine checker, Func<char, bool> primaryCanEmit)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _checker = checker ?? throw new ArgumentNullException(nameof(checker));
        _primaryCanEmit = primaryCanEmit ?? throw new ArgumentNullException(nameof(primaryCanEmit));
    }

    public string Name => $"cross-checked:{_primary.Name}+{_checker.Name}";

    public async Task<OcrResult> RecognizeAsync(ImageCrop crop, CancellationToken cancellationToken)
    {
        var primaryTask = _primary.RecognizeAsync(crop, cancellationToken);
        var checkerTask = _checker.RecognizeAsync(crop, cancellationToken);
        var primary = await primaryTask.ConfigureAwait(false);
        var checker = await checkerTask.ConfigureAwait(false);

        var primaryText = HasText(primary) ? primary.Text : string.Empty;
        var checkerText = HasText(checker) ? checker.Text : string.Empty;
        if (primaryText.Length == 0 && checkerText.Length == 0)
        {
            // Neither saw text: a sticker, image or voice note.
            return primary.Status == OcrTextStatus.Unsupported
                ? primary
                : primary with { Status = OcrTextStatus.NoText };
        }

        var primaryKey = OcrTextNormalizer.ComparisonKey(primaryText);
        var checkerKey = OcrTextNormalizer.ComparisonKey(checkerText);
        if (primaryKey.Length > 0 && primaryKey == checkerKey)
        {
            return primary with { Status = OcrTextStatus.Recognized };
        }

        if (primaryKey.Length > 0 && DiffersOnlyByUnemittable(primaryKey, checkerKey))
        {
            return new OcrResult(checker.Text, primary.OcrConfidence, OcrTextStatus.Recognized, checker.RawText);
        }

        if (primaryKey.Length > 0 && checkerKey.Length > 0 &&
            OcrTextNormalizer.EditDistance(primaryKey, checkerKey)
                <= Math.Max(1, (int)(primaryKey.Length * ToleratedDisagreement)))
        {
            return primary with { Status = OcrTextStatus.Recognized };
        }

        var shown = primaryText.Length > 0 ? primary : checker;
        return shown with { Status = OcrTextStatus.LowConfidence };
    }

    /// <summary>
    /// True when <paramref name="checker"/> is <paramref name="primary"/> with extra
    /// characters inserted, every one of which the primary engine has no way to output.
    /// </summary>
    private bool DiffersOnlyByUnemittable(string primary, string checker)
    {
        if (checker.Length <= primary.Length)
        {
            return false;
        }

        var matched = 0;
        foreach (var character in checker)
        {
            if (matched < primary.Length && character == primary[matched])
            {
                matched++;
            }
            else if (_primaryCanEmit(character))
            {
                return false;
            }
        }

        return matched == primary.Length;
    }

    private static bool HasText(OcrResult result) =>
        result.Status is OcrTextStatus.Recognized or OcrTextStatus.LowConfidence &&
        !string.IsNullOrWhiteSpace(result.Text);
}
