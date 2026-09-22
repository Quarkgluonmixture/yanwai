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
