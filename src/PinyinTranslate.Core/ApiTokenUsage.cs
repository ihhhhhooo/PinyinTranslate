using System.Text.Json;

namespace PinyinTranslate.Core;

public sealed record ApiTokenUsage(int InputTokens, int OutputTokens, int? CacheHitTokens = null, int? CacheMissTokens = null)
{
    public (int Hit, int Miss, bool Known) CacheBreakdown()
    {
        bool validHit = CacheHitTokens is >= 0 && CacheHitTokens <= InputTokens;
        bool validMiss = CacheMissTokens is >= 0 && CacheMissTokens <= InputTokens;
        if (CacheHitTokens is not null && CacheMissTokens is not null)
            return validHit && validMiss && (long)CacheHitTokens.Value + CacheMissTokens.Value == InputTokens
                ? (CacheHitTokens.Value, CacheMissTokens.Value, true) : (0, InputTokens, false);
        if (validHit) return (CacheHitTokens!.Value, InputTokens - CacheHitTokens.Value, true);
        if (validMiss) return (InputTokens - CacheMissTokens!.Value, CacheMissTokens.Value, true);
        return (0, InputTokens, InputTokens == 0);
    }

    public static bool TryParse(JsonElement element, out ApiTokenUsage? usage)
    {
        usage = null;
        if (element.ValueKind != JsonValueKind.Object || !Count(element, "prompt_tokens", out int input) ||
            !Count(element, "completion_tokens", out int output)) return false;
        int? hit = OptionalCount(element, "prompt_cache_hit_tokens");
        int? miss = OptionalCount(element, "prompt_cache_miss_tokens");
        if (hit is null && miss is null && element.TryGetProperty("prompt_tokens_details", out var details) &&
            details.ValueKind == JsonValueKind.Object) hit = OptionalCount(details, "cached_tokens");
        usage = new(input, output, hit, miss);
        return true;
    }

    private static bool Count(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value) && value >= 0;
    }
    private static int? OptionalCount(JsonElement element, string name) => !element.TryGetProperty(name, out _)
        ? null : Count(element, name, out int value) ? value : -1;
}
