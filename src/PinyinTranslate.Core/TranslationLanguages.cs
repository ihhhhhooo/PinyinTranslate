using System.Text.Json;

namespace PinyinTranslate.Core;

public sealed record TranslationLanguage(string Code, string DisplayName, string PromptName);

/// <summary>Stable identifiers shared by settings, prompts, caches and result labels.</summary>
public static class TranslationLanguages
{
    public const string SimplifiedChinese = "zh-Hans";
    public const string TraditionalChinese = "zh-Hant";
    public const string English = "en";

    public static IReadOnlyList<TranslationLanguage> All { get; } = Array.AsReadOnly<TranslationLanguage>([
        new(SimplifiedChinese, "中文（简体）", "Simplified Chinese"),
        new(TraditionalChinese, "中文（繁体）", "Traditional Chinese"),
        new(English, "英语", "English"),
        new("ja", "日语", "Japanese"),
        new("ko", "韩语", "Korean"),
        new("fr", "法语", "French"),
        new("de", "德语", "German"),
        new("es", "西班牙语", "Spanish"),
        new("pt", "葡萄牙语", "Portuguese"),
        new("ru", "俄语", "Russian"),
        new("vi", "越南语", "Vietnamese")
    ]);

    public static bool IsSupported(string? code) => All.Any(language => language.Code == code);

    public static TranslationLanguage Get(string code) => All.FirstOrDefault(language => language.Code == code)
        ?? throw new ArgumentException("不支持所选翻译语种。", nameof(code));

    public static void ValidatePair(string languageA, string languageB)
    {
        _ = Get(languageA); _ = Get(languageB);
        if (languageA == languageB) throw new ArgumentException("划词互译需要选择两种不同的语言；中文简体和繁体可分别选择。");
    }

    internal static string TargetInstruction(TranslationLanguage language) =>
        $"The target language is {language.PromptName} ({language.Code}). " + ScriptInstruction(language.Code);

    internal static string ScriptInstruction(string code) => code switch
    {
        SimplifiedChinese => "Use Simplified Chinese characters consistently. ",
        TraditionalChinese => "Use Traditional Chinese characters consistently. ",
        _ => ""
    };
}

/// <summary>The actual target travels with the result, including through the cache.</summary>
public sealed record TextTranslation(string Translation, string TargetLanguage)
{
    public string ToCacheValue() => JsonSerializer.Serialize(new { target_language = TargetLanguage, translation = Translation });

    public static TextTranslation Parse(string json, string languageA, string languageB)
    {
        TranslationLanguages.ValidatePair(languageA, languageB);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? target = root.GetProperty("target_language").GetString();
            string? text = root.GetProperty("translation").GetString();
            if ((target != languageA && target != languageB) || string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("互译结果缺少有效译文或目标语种不属于所选语言对。");
            return new(text.Trim(), target);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("互译结果格式不完整，请重新划选重试。", error); }
    }
}
