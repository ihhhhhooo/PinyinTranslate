using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PinyinTranslate.Core;

namespace PinyinTranslate.Core.Tests;

// Temporary synthetic ledgers, a controlled clock, and fake HTTP only.
public sealed class ApiUsageLedgerTests
{
    [Theory]
    [InlineData("2026-11-02T11:59:59+08:00", "2026-11-02T12:00:01+08:00", "8.53")]
    [InlineData("2026-11-02T08:59:59+08:00", "2026-11-02T09:00:01+08:00", "4.265")]
    [InlineData("2026-11-02T23:59:59+08:00", "2026-11-03T09:00:01+08:00", "4.265")]
    public void ACompletionUsesTheRequestStartBandAndBeijingDate(string start, string end, string cost)
    {
        var clock = new Clock(start);
        var path = NewPath();
        var ledger = new ApiUsageLedger(path, clock);
        var id = ledger.Reserve("fake", 1, 1);
        clock.Now = DateTimeOffset.Parse(end, CultureInfo.InvariantCulture);
        ledger.Complete(id, new ApiTokenUsage(1_000_000, 1_000_000, 750_000, 250_000));
        var day = Assert.Single(ledger.Snapshot().Days);
        Assert.Equal("2026-11-02", day.Day);
        Assert.Equal(decimal.Parse(cost, CultureInfo.InvariantCulture), day.EstimatedCny);
        Assert.Equal(750_000, day.CacheHitTokens); Assert.Equal(250_000, day.CacheMissTokens);
        Assert.Equal(0, day.MissingCacheDetails);
        Assert.Equal(day, Assert.Single(new ApiUsageLedger(path, clock).Snapshot().Days));
    }

    [Fact]
    public void MissingCacheDetailsUseALabeledUpperEstimateAndDuplicateCompletionsDoNotDoubleCount()
    {
        var ledger = new ApiUsageLedger(NewPath(), new Clock("2026-11-02T09:00:00+08:00"));
        var id = ledger.Reserve("fake", 1, 1);
        ledger.Complete(id, 1_000_000, 1_000_000);
        ledger.Complete(id, 1_000_000, 1_000_000);
        ledger.MarkUnknown(id);
        var day = Assert.Single(ledger.Snapshot().Days);
        Assert.Equal(10m, day.EstimatedCny); Assert.Equal(1, day.Requests);
        Assert.Equal(1, day.MissingCacheDetails); Assert.Equal(1_000_000, day.UnclassifiedInputTokens);
        Assert.Equal(0, day.CacheHitTokens); Assert.Equal(0, day.CacheMissTokens);
    }

    [Fact]
    public void AVersionOneLedgerIsPreservedAndBackedUpWithoutInventingOldCacheOrTimingData()
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = Guid.NewGuid();
        string original = JsonSerializer.Serialize(new
        {
            Version = 1,
            Days = new Dictionary<string, object> { ["2026-11-06"] = new
                { Requests = 4, WithUsage = 2, Unknown = 1, InputTokens = 1000, OutputTokens = 200, EstimatedCny = .0036m } },
            Pending = new Dictionary<Guid, string> { [pending] = "2026-11-06" }
        });
        File.WriteAllText(path, original);
        var ledger = new ApiUsageLedger(path, new Clock("2026-11-06T09:00:00+08:00"));
        var old = Assert.Single(ledger.Snapshot().Days);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(.0036m, old.LegacyEstimatedCny); Assert.Equal(0m, old.EstimatedCny);
        Assert.Equal(2, old.LegacyWithUsage); Assert.Equal(2, old.MissingUsage);
        Assert.Equal(1000, old.InputTokens); Assert.Equal(200, old.OutputTokens);
        var id = ledger.Reserve("synthetic request, never stored", 1, 1);
        ledger.Complete(id, new ApiTokenUsage(100, 50, 0, 100));
        var current = Assert.Single(ledger.Snapshot().Days);
        Assert.Equal(.0006m, current.EstimatedCny); Assert.Equal(.0036m, current.LegacyEstimatedCny);
        Assert.Equal(original, File.ReadAllText(path + ".v1-backup.json"));
        Assert.Equal(current, Assert.Single(new ApiUsageLedger(path).Snapshot().Days));
        Assert.DoesNotContain("synthetic request", File.ReadAllText(path));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, json.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public void ARestartPreservesUnknownRequestsAndCannotCompleteAnOldInFlightId()
    {
        string path = NewPath();
        var clock = new Clock("2026-11-06T09:00:00+08:00");
        var before = new ApiUsageLedger(path, clock);
        var old = before.Reserve("fake", 1, 1);
        var after = new ApiUsageLedger(path, clock);
        after.Complete(old, new ApiTokenUsage(100, 1, 0, 100));
        var id = after.Reserve("new fake", 1, 1);
        after.Complete(id, new ApiTokenUsage(100, 1, 0, 100));
        var day = Assert.Single(after.Snapshot().Days);
        Assert.Equal(2, day.Requests); Assert.Equal(1, day.MissingUsage);
        Assert.Equal(100, day.InputTokens); Assert.Equal(.000208m, day.EstimatedCny);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Version\":\"invalid\"}")]
    [InlineData("{\"Version\":99,\"Days\":{},\"Pending\":{}}")]
    [InlineData("{\"Version\":2,\"Days\":{\"2026-11-06\":{\"Requests\":1}},\"Pending\":{}}")]
    public void CorruptHistoryIsNeverOverwrittenByNewStatistics(string original)
    {
        string path = NewPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, original);
        var ledger = new ApiUsageLedger(path, new Clock("2026-11-06T09:00:00+08:00"));
        var id = ledger.Reserve("fake", 1, 1); ledger.Complete(id, new ApiTokenUsage(100, 1, 100, 0));
        Assert.NotNull(ledger.Snapshot().Warning);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(.000012m, Assert.Single(ledger.Snapshot().Days).EstimatedCny);
    }

    [Fact]
    public async Task ConcurrentCompletionsKeepTheTotalsAndPersistedLedgerConsistent()
    {
        string path = NewPath();
        var ledger = new ApiUsageLedger(path, new Clock("2026-11-06T09:00:00+08:00"));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            var id = ledger.Reserve("fake", 1, 1);
            ledger.Complete(id, new ApiTokenUsage(10, 5, 3, 7));
        })));
        var day = Assert.Single(ledger.Snapshot().Days);
        Assert.Equal(20, day.Requests); Assert.Equal(60, day.CacheHitTokens); Assert.Equal(140, day.CacheMissTokens);
        Assert.Equal(.0010824m, day.EstimatedCny);
        Assert.Equal(day, Assert.Single(new ApiUsageLedger(path).Snapshot().Days));
    }

    [Theory]
    [InlineData("\"prompt_cache_hit_tokens\":900,\"prompt_cache_miss_tokens\":100", ".001836", 0)]
    [InlineData("\"prompt_tokens_details\":{\"cached_tokens\":900}", ".001836", 0)]
    [InlineData("\"prompt_cache_hit_tokens\":1001", ".0036", 1)]
    public async Task TheActualClientDeliversCacheUsageToTheNormalLedger(string fields, string expected, int missing)
    {
        var ledger = new ApiUsageLedger(NewPath(), new Clock("2026-11-06T09:00:00+08:00"));
        using var http = new HttpClient(new Handler("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"translated\"}}],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":200," + fields + "}}"));
        var client = new DeepSeekClient(http, ledger, () => "fake-test-key");
        Assert.Equal("translated", await client.TranslateAsync("fake source", true, "fake"));
        var day = Assert.Single(ledger.Snapshot().Days);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), day.EstimatedCny);
        Assert.Equal(missing, day.MissingCacheDetails); Assert.Equal(0, day.MissingUsage);
    }

    [Fact]
    public void UnknownFutureCalendarsAreFlaggedInStoredUsage()
    {
        var ledger = new ApiUsageLedger(NewPath(), new Clock("2028-01-03T10:00:00+08:00"));
        var id = ledger.Reserve("fake", 1, 1); ledger.Complete(id, new ApiTokenUsage(1, 1, 0, 1));
        Assert.Equal(1, Assert.Single(ledger.Snapshot().Days).MissingCalendar);
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), "PinyinTranslate-pricing-tests", Guid.NewGuid().ToString("N"), "usage.json");
    private sealed class Clock(string time) : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse(time, CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
    private sealed class Handler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
    }
}
