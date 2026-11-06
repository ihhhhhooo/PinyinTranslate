using System.Globalization;
using System.Text.Json;
using PinyinTranslate.Core;

namespace PinyinTranslate.Core.Tests;

public sealed class FlashPricingTests
{
    [Theory]
    [InlineData("2026-11-02T08:59:59+08:00", false)]
    [InlineData("2026-11-02T09:00:00+08:00", true)]
    [InlineData("2026-11-02T11:59:59+08:00", true)]
    [InlineData("2026-11-02T12:00:00+08:00", false)]
    [InlineData("2026-11-02T13:59:59+08:00", false)]
    [InlineData("2026-11-02T14:00:00+08:00", true)]
    [InlineData("2026-11-02T17:59:59+08:00", true)]
    [InlineData("2026-11-02T18:00:00+08:00", false)]
    [InlineData("2026-11-07T10:00:00+08:00", false)]
    [InlineData("2026-11-08T15:00:00+08:00", false)]
    [InlineData("2026-02-14T10:00:00+08:00", false)]
    [InlineData("2026-10-10T15:00:00+08:00", false)]
    [InlineData("2026-02-20T10:00:00+08:00", false)]
    [InlineData("2026-10-06T15:00:00+08:00", false)]
    [InlineData("2026-10-08T09:00:00+08:00", true)]
    [InlineData("2027-06-09T10:00:00+08:00", false)]
    [InlineData("2027-09-15T15:00:00+08:00", false)]
    [InlineData("2027-02-14T10:00:00+08:00", false)]
    public void BeijingBoundariesHolidaysAndCompensatoryWeekendsChooseTheCorrectBand(string instant, bool peak)
    {
        var quote = FlashPricing.At(Parse(instant));
        Assert.Equal(peak, quote.IsPeak); Assert.True(quote.CalendarKnown);
    }

    [Fact]
    public void TimeZoneOffsetsDoNotChangeTheBandForTheSameInstant()
    {
        var utc = Parse("2026-11-02T01:00:00Z");
        Assert.Equal(FlashPricing.At(utc), FlashPricing.At(utc.ToOffset(TimeSpan.FromHours(-5))));
        Assert.True(FlashPricing.At(utc).IsPeak);
        Assert.Equal(new DateOnly(2026, 11, 2), FlashPricing.BeijingDate(Parse("2026-11-01T16:00:00Z")));
    }

    [Fact]
    public void FlashUsesTheLeftColumnAndPreservesOneTokenPrecision()
    {
        var peak = FlashPricing.At(Parse("2026-11-02T09:00:00+08:00"));
        var idle = FlashPricing.At(Parse("2026-11-02T12:00:00+08:00"));
        Assert.Equal(.02m, FlashPricing.Estimate(new(1_000_000, 0, 1_000_000, 0), idle));
        Assert.Equal(.04m, FlashPricing.Estimate(new(1_000_000, 0, 1_000_000, 0), peak));
        Assert.Equal(1m, FlashPricing.Estimate(new(1_000_000, 0, 0, 1_000_000), idle));
        Assert.Equal(2m, FlashPricing.Estimate(new(1_000_000, 0, 0, 1_000_000), peak));
        Assert.Equal(4m, FlashPricing.Estimate(new(0, 1_000_000), idle));
        Assert.Equal(8m, FlashPricing.Estimate(new(0, 1_000_000), peak));
        Assert.Equal(.00000002m, FlashPricing.Estimate(new(1, 0, 1, 0), idle));
        Assert.Equal(8.314m, FlashPricing.Estimate(new(1_000_000, 2_000_000, 700_000, 300_000), idle));
        Assert.Equal(16.628m, FlashPricing.Estimate(new(1_000_000, 2_000_000, 700_000, 300_000), peak));
    }

    [Theory]
    [InlineData("2028-01-03T10:00:00+08:00", true, false)]
    [InlineData("2028-01-01T10:00:00+08:00", false, true)]
    [InlineData("2028-01-03T08:00:00+08:00", false, true)]
    public void UnpublishedCalendarsAreFlaggedOnlyWhenTheyCouldChangeTheRate(string time, bool peak, bool known)
    {
        var quote = FlashPricing.At(Parse(time));
        Assert.Equal(peak, quote.IsPeak); Assert.Equal(known, quote.CalendarKnown);
    }

    [Theory]
    [InlineData("\"prompt_cache_hit_tokens\":80,\"prompt_cache_miss_tokens\":20", 80, 20, true)]
    [InlineData("\"prompt_cache_hit_tokens\":80", 80, 20, true)]
    [InlineData("\"prompt_cache_miss_tokens\":20", 80, 20, true)]
    [InlineData("\"prompt_tokens_details\":{\"cached_tokens\":80}", 80, 20, true)]
    [InlineData("\"unused\":0", 0, 100, false)]
    [InlineData("\"prompt_cache_hit_tokens\":80,\"prompt_cache_miss_tokens\":50", 0, 100, false)]
    [InlineData("\"prompt_cache_hit_tokens\":101", 0, 100, false)]
    [InlineData("\"prompt_cache_hit_tokens\":-1", 0, 100, false)]
    [InlineData("\"prompt_cache_hit_tokens\":\"bad\",\"prompt_tokens_details\":{\"cached_tokens\":80}", 0, 100, false)]
    public void ApiCacheFieldsAreValidatedBeforeTheyAffectAnEstimate(string fields, int hit, int miss, bool known)
    {
        using var document = JsonDocument.Parse("{\"prompt_tokens\":100,\"completion_tokens\":5," + fields + "}");
        Assert.True(ApiTokenUsage.TryParse(document.RootElement, out var usage));
        Assert.Equal((hit, miss, known), usage!.CacheBreakdown());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"prompt_tokens\":\"100\",\"completion_tokens\":1}")]
    [InlineData("{\"prompt_tokens\":-1,\"completion_tokens\":1}")]
    public void InvalidTokenTotalsRemainUnknown(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(ApiTokenUsage.TryParse(document.RootElement, out _));
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
