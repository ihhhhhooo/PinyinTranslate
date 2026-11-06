using System.Globalization;
using PinyinTranslate.App;
using PinyinTranslate.Core;

namespace PinyinTranslate.Screenshot.Tests;

public sealed class ApiStatisticsCalculatorTests
{
    [Fact]
    public void CorrectedAndHistoricalCostsAreSeparatedAndTodayUsesBeijingTime()
    {
        var now = DateTimeOffset.Parse("2026-11-05T16:30:00Z", CultureInfo.InvariantCulture);
        var usage = new ApiUsageSnapshot([new("2026-11-06", 2, 1, 1000, 200, .001m)
            { CacheHitTokens = 700, CacheMissTokens = 300, LegacyWithUsage = 1, LegacyEstimatedCny = .003m }], null);
        var legacy = new BudgetSnapshot("synthetic", 1, 200,
        [
            new(Guid.NewGuid(), now, "deepseek-flash", "fake", 100, 100, .001m, "complete", 50, 40, .00042m),
            new(Guid.NewGuid(), now.AddDays(-1), "deepseek-flash", "fake", 100, 100, .001m, "unknown")
        ]);
        var result = ApiStatisticsCalculator.Create(usage, legacy, now);
        Assert.Equal(.001m, result.EstimatedCny); Assert.Equal(.001m, result.TodayEstimatedCny);
        Assert.Equal(.00342m, result.LegacyEstimatedCny); Assert.Equal(2, result.LegacyPricingRequests);
        Assert.Equal(4, result.Requests); Assert.Equal(3, result.TodayRequests);
        Assert.Equal(1050, result.InputTokens); Assert.Equal(240, result.OutputTokens);
        Assert.Equal(1290, result.TodayTokens); Assert.Equal(2, result.MissingUsage);
        Assert.Equal(700, result.CacheHitTokens); Assert.Equal(300, result.CacheMissTokens);
        Assert.False(result.CurrentPricing!.IsPeak);
    }

    [Fact]
    public void MissingCacheAndCalendarMetadataRemainVisibleToTheUi()
    {
        var usage = new ApiUsageSnapshot([new("2028-01-03", 1, 0, 10, 5, .00006m)
            { UnclassifiedInputTokens = 10, MissingCacheDetails = 1, MissingCalendar = 1 }], null);
        var result = ApiStatisticsCalculator.Create(usage, null, DateTimeOffset.Parse("2028-01-03T10:00:00+08:00", CultureInfo.InvariantCulture));
        Assert.Equal(10, result.UnclassifiedInputTokens); Assert.Equal(1, result.MissingCacheDetails);
        Assert.Equal(1, result.MissingCalendar); Assert.False(result.CurrentPricing!.CalendarKnown);
    }
}
