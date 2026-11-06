using System.Globalization;
using PinyinTranslate.Core;

namespace PinyinTranslate.App;

/// <summary>Pure aggregation; historical fixed-price estimates never enter the corrected-price total.</summary>
internal static class ApiStatisticsCalculator
{
    public static ApiStatistics Create(ApiUsageSnapshot usage, BudgetSnapshot? legacy, DateTimeOffset now)
    {
        var entries = legacy?.Entries ?? [];
        var today = FlashPricing.BeijingDate(now);
        string dayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var oldToday = entries.Where(x => FlashPricing.BeijingDate(x.Time) == today).ToArray();
        var newToday = usage.Days.Where(x => x.Day == dayKey).ToArray();
        return new(
            usage.Days.Sum(x => x.Requests) + entries.Count,
            usage.Days.Sum(x => x.InputTokens) + entries.Sum(x => (long)(x.InputTokens ?? 0)),
            usage.Days.Sum(x => x.OutputTokens) + entries.Sum(x => (long)(x.OutputTokens ?? 0)),
            usage.Days.Sum(x => x.EstimatedCny),
            usage.Days.Sum(x => x.MissingUsage) + entries.LongCount(x => x.Status != "complete"),
            newToday.Sum(x => x.Requests) + oldToday.Length,
            newToday.Sum(x => x.InputTokens + x.OutputTokens) + oldToday.Sum(x => (long)(x.InputTokens ?? 0) + (x.OutputTokens ?? 0)),
            newToday.Sum(x => x.EstimatedCny))
        {
            CacheHitTokens = usage.Days.Sum(x => x.CacheHitTokens),
            CacheMissTokens = usage.Days.Sum(x => x.CacheMissTokens),
            UnclassifiedInputTokens = usage.Days.Sum(x => x.UnclassifiedInputTokens),
            MissingCacheDetails = usage.Days.Sum(x => x.MissingCacheDetails),
            MissingCalendar = usage.Days.Sum(x => x.MissingCalendar),
            LegacyPricingRequests = usage.Days.Sum(x => x.LegacyWithUsage) + entries.LongCount(x => x.Status == "complete"),
            LegacyEstimatedCny = usage.Days.Sum(x => x.LegacyEstimatedCny) + entries.Sum(x => x.KnownCny ?? 0m),
            CurrentPricing = FlashPricing.At(now)
        };
    }
}
