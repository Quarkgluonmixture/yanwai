using System.Text;
using System.Text.RegularExpressions;

namespace Yanwai.Ocr;

public static partial class OcrTextNormalizer
{
    public static string Normalize(string? text)
    {
        var compact = Whitespace().Replace(text?.Trim() ?? string.Empty, " ");
        if (compact.Length < 3)
        {
            return compact;
        }

        var output = new StringBuilder(compact.Length);
        for (var index = 0; index < compact.Length; index++)
        {
            var current = compact[index];
            if (current == '¦')
            {
                continue;
            }

            if (current == ' ' &&
                index > 0 &&
                index + 1 < compact.Length &&
                ShouldRemoveSpace(compact[index - 1], compact[index + 1]))
            {
                continue;
            }

            output.Append(current);
        }

        return RemoveCjkBackslashArtifacts(output.ToString()).Trim(' ', '|', '¦');
    }

    /// <summary>
    /// A key for asking "did two engines read the same thing": full-width folded to
    /// half-width, whitespace dropped, case folded. Engines disagree on those freely
    /// while agreeing on the text; nothing else is folded.
    /// </summary>
    public static string ComparisonKey(string? text)
    {
        var folded = (text ?? string.Empty).Normalize(NormalizationForm.FormKC);
        var output = new StringBuilder(folded.Length);
        foreach (var character in folded)
        {
            if (!char.IsWhiteSpace(character))
            {
                output.Append(char.ToLowerInvariant(character));
            }
        }

        return output.ToString();
    }

    /// <summary>Character edit distance (insert, delete, substitute all cost 1).</summary>
    public static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var column = 0; column <= b.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= a.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= b.Length; column++)
            {
                var substitutionCost = a[row - 1] == b[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static string RemoveCjkBackslashArtifacts(string value)
    {
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' &&
                index > 0 &&
                index + 1 < value.Length &&
                IsCjk(value[index - 1]) &&
                IsCjk(value[index + 1]))
            {
                continue;
            }

            output.Append(value[index]);
        }

        return output.ToString();
    }

    private static bool ShouldRemoveSpace(char previous, char next) =>
        (IsCjk(previous) && IsCjk(next)) ||
        (IsCjk(previous) && IsCjkPunctuation(next)) ||
        (IsCjkPunctuation(previous) && IsCjk(next));

    private static bool IsCjk(char value) => value is
        >= '\u3400' and <= '\u4DBF' or
        >= '\u4E00' and <= '\u9FFF' or
        >= '\uF900' and <= '\uFAFF';

    private static bool IsCjkPunctuation(char value) =>
        (value is >= '\u3000' and <= '\u303F' or >= '\uFF01' and <= '\uFF65') &&
        char.IsPunctuation(value);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
