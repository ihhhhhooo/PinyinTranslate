using System.Text;
using System.Text.RegularExpressions;

namespace PinyinTranslate.Core;

/// <summary>Legacy CN/EN helper for callers of the bool client overload; desktop selections use a model-resolved language pair.</summary>
public static class SelectionTranslationLanguage
{
    private static readonly Regex NonProse = new(@"https?://[^\s<>]+|www\.[^\s<>]+|`[^`\r\n]*`",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    public static bool ShouldTranslateToChinese(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string prose = NonProse.Replace(text, " ");
        int chinese = 0, englishWords = 0;
        bool inEnglishWord = false, otherEastAsian = false;
        foreach (var rune in prose.EnumerateRunes())
        {
            int value = rune.Value;
            bool english = value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
            if (english && !inEnglishWord) englishWords++;
            inEnglishWord = english;
            if (value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or
                >= 0x20000 and <= 0x323AF or 0x3007) chinese++;
            if (value is >= 0x3040 and <= 0x30FF or >= 0xAC00 and <= 0xD7AF) otherEastAsian = true;
        }
        // Count English words rather than letters so a proper name or API acronym
        // does not outweigh a Chinese sentence. Ties with Chinese favor CN -> EN.
        return otherEastAsian || chinese == 0 || englishWords > chinese;
    }
}
