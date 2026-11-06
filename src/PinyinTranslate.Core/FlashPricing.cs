namespace PinyinTranslate.Core;

public sealed record FlashPriceQuote(bool IsPeak, bool CalendarKnown, decimal CacheHitCnyPerMillion,
    decimal CacheMissCnyPerMillion, decimal OutputCnyPerMillion);

/// <summary>Flash CNY prices, using Beijing time independently of the device time zone.</summary>
public static class FlashPricing
{
    public const string PriceVersion = "flash-cny-2026-09-10";
    public const string CalendarCoverage = "2026–2027";
    public static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    // State Council holiday periods, not workday overrides: weekends stay off-peak
    // even when a Saturday/Sunday is designated as a compensatory working day.
    private static readonly Dictionary<int, (int Start, int End)[]> Holidays = new()
    {
        [2026] = [(101, 103), (215, 223), (404, 406), (501, 505), (619, 621), (925, 927), (1001, 1007)],
        [2027] = [(101, 103), (205, 213), (403, 405), (501, 505), (609, 609), (915, 915), (1001, 1007)]
    };

    public static DateOnly BeijingDate(DateTimeOffset time) => DateOnly.FromDateTime(time.ToOffset(BeijingOffset).DateTime);

    public static FlashPriceQuote At(DateTimeOffset requestTime)
    {
        var local = requestTime.ToOffset(BeijingOffset);
        bool weekday = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        bool peakHours = local.TimeOfDay >= TimeSpan.FromHours(9) && local.TimeOfDay < TimeSpan.FromHours(12) ||
                         local.TimeOfDay >= TimeSpan.FromHours(14) && local.TimeOfDay < TimeSpan.FromHours(18);
        bool knownYear = Holidays.TryGetValue(local.Year, out var holidays);
        int monthDay = local.Month * 100 + local.Day;
        bool holiday = holidays?.Any(range => monthDay >= range.Start && monthDay <= range.End) == true;
        bool peak = weekday && peakHours && !holiday;
        // Outside peak windows, holidays cannot affect the price. For an unknown
        // future calendar, use a flagged peak upper estimate instead of inventing dates.
        bool calendarKnown = knownYear || !weekday || !peakHours;
        return peak ? new(true, calendarKnown, .04m, 2m, 8m) : new(false, calendarKnown, .02m, 1m, 4m);
    }

    public static decimal Estimate(ApiTokenUsage usage, FlashPriceQuote quote)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.InputTokens < 0 || usage.OutputTokens < 0) throw new ArgumentOutOfRangeException(nameof(usage));
        var (hit, miss, _) = usage.CacheBreakdown();
        return (hit * quote.CacheHitCnyPerMillion + miss * quote.CacheMissCnyPerMillion +
                usage.OutputTokens * quote.OutputCnyPerMillion) / 1_000_000m;
    }
}
