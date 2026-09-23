using System.IO;
using System.Text;

namespace Yanwai.Ocr;

/// <summary>
/// Reads <c>PostProcess.character_dict</c> out of a PaddleX <c>inference.yml</c>.
///
/// Not a YAML parser: the dictionary is a flat list whose entries are either plain or
/// single-quoted, which is all the published PP-OCR models use. Anything else is an
/// error rather than a guess, because one misread entry shifts every character after
/// it.
/// </summary>
public static class PaddleCharacterDictionary
{
    private const string Header = "  character_dict:";
    private const string ItemPrefix = "  - ";

    public static IReadOnlyList<string> Read(string path)
    {
        var entries = new List<string>();
        var inDictionary = false;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (!inDictionary)
            {
                inDictionary = line == Header;
                continue;
            }

            if (!line.StartsWith(ItemPrefix, StringComparison.Ordinal))
            {
                break;
            }

            entries.Add(Scalar(line[ItemPrefix.Length..]));
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException($"No character_dict found in {path}.");
        }

        return entries;
    }

    private static string Scalar(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (value.Length == 0 || value[0] is '"' or '\'')
        {
            throw new InvalidDataException($"Unsupported dictionary entry: {value}");
        }

        // Plain scalar. "0" or "~" mean the character itself here, not a number or null.
        return value;
    }
}
