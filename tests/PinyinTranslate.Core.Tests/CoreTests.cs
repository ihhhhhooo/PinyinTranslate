using System.Net;
using System.Text;
using PinyinTranslate.Core;

namespace PinyinTranslate.Core.Tests;

public sealed class CoreTests
{
    private static string LedgerPath() => Path.Combine(Path.GetTempPath(), "PinyinTranslate-tests", Guid.NewGuid().ToString("N"), "budget.json");
    [Fact]
    public void ReservationIsPersistentAndCountsFailures()
    {
        var path = LedgerPath(); var ledger = new BudgetLedger(path, 1, 2);
        var first = ledger.Reserve("sample-1", 500, 100); ledger.MarkUnknown(first);
        var second = new BudgetLedger(path, 1, 2).Reserve("sample-2", 500, 100);
        ledger.Complete(second, 50, 40);
        var s = new BudgetLedger(path, 1, 2).Snapshot();
        Assert.Equal(2, s.Entries.Count); Assert.Equal("unknown", s.Entries[0].Status); Assert.Null(s.Entries[0].KnownCny);
        Assert.Equal("complete", s.Entries[1].Status); Assert.Equal(50, s.Entries[1].InputTokens);
        Assert.Throws<InvalidOperationException>(() => ledger.Reserve("sample-3", 10, 10));
    }
    [Fact]
    public async Task ConcurrentReservationsCannotExceedLimit()
    {
        var path = LedgerPath();
        var attempts = Enumerable.Range(0, 30).Select(i => Task.Run(() =>
        {
            try { new BudgetLedger(path, 1m, 5).Reserve("parallel-" + i, 100, 100); return true; }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.Equal(5, (await Task.WhenAll(attempts)).Count(x => x));
        Assert.Equal(5, new BudgetLedger(path, 1m, 5).Snapshot().Entries.Count);
    }
    [Fact]
    public void InsufficientBudgetAndCorruptionFailClosed()
    {
        var path = LedgerPath(); var b = new BudgetLedger(path, .0001m, 200);
        Assert.Throws<InvalidOperationException>(() => b.Reserve("too-expensive", 100, 100));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{broken");
        Assert.Throws<InvalidDataException>(() => b.Reserve("bad-ledger", 1, 1));
    }
    [Fact]
    public void BalanceCheckRecordsOnlyStructuredMetadataAndPreservesEntries()
    {
        var path = LedgerPath(); var ledger = new BudgetLedger(path);
        ledger.Reserve("sample", 100, 100);
        ledger.RecordBalanceCheck(true, 2.50m);
        var snapshot = new BudgetLedger(path).Snapshot();
        Assert.Contains("人民币可用余额=2.50", snapshot.BalanceCheck);
        Assert.Single(snapshot.Entries);
        Assert.Throws<ArgumentException>(() => ledger.RecordBalanceCheck(false, 3m));
        ledger.RecordBalanceCheck(false);
        Assert.Contains("失败或不可用", ledger.Snapshot().BalanceCheck);
    }
    [Fact]
    public void SecretRoundTripsThroughCurrentUserDpapi()
    {
        var path = Path.Combine(Path.GetDirectoryName(LedgerPath())!, "test.dpapi");
        SecretStore.Save(path, "fake-test-key");
        Assert.DoesNotContain("fake-test-key", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Equal("fake-test-key", SecretStore.Load(path));
    }
    [Fact]
    public void OptionalProvidedCredentialCanBeDecryptedWithoutPrintingIt()
    {
        var path = Environment.GetEnvironmentVariable("PINYINTRANSLATE_TEST_CREDENTIAL");
        if (path is not null) Assert.False(string.IsNullOrWhiteSpace(SecretStore.Load(path)));
    }
    [Fact]
    public async Task ClientUsesRequiredWireFormatAndBillsReturnedUsage()
    {
        string? body = null;
        var handler = new Handler(async (req, ct) =>
        {
            body = await req.Content!.ReadAsStringAsync(ct);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"belong to\"}}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":5}}", Encoding.UTF8, "application/json") };
        });
        var ledger = new BudgetLedger(LedgerPath()); var client = new DeepSeekClient(new HttpClient(handler), ledger, () => "fake");
        Assert.Equal("belong to", await client.TranslateAsync("属于", false, "case"));
        Assert.Contains("deepseek-flash", body); Assert.Contains("disabled", body); Assert.Contains("max_tokens", body);
        Assert.Equal("complete", Assert.Single(ledger.Snapshot().Entries).Status);
    }
    [Fact]
    public async Task TruncatedResponseRejectedAndAccounted()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"unfinished\"}}],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":5}}") }));
        var ledger = new BudgetLedger(LedgerPath()); var client = new DeepSeekClient(new HttpClient(handler), ledger, () => "fake");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.TranslateAsync("test", true, "truncated"));
        Assert.Equal("complete", Assert.Single(ledger.Snapshot().Entries).Status);
    }
    [Fact]
    public async Task FailureOrCancellationKeepsReservation()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("network"));
        var ledger = new BudgetLedger(LedgerPath()); var client = new DeepSeekClient(new HttpClient(handler), ledger, () => "fake");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.TranslateAsync("test", true, "network"));
        Assert.Equal("unknown", Assert.Single(ledger.Snapshot().Entries).Status);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TranslateAsync("test", true, "cancel", cancelled.Token));
        Assert.Single(ledger.Snapshot().Entries);
    }
    [Fact]
    public async Task ImageRejectsDuplicateIdsAndEmptyTranslations()
    {
        const string inner = "{\"groups\":[{\"id\":\"1\",\"source\":\"One.\",\"translation\":\"一。\",\"uncertain\":false},{\"id\":\"1\",\"source\":\"Two.\",\"translation\":\"二。\",\"uncertain\":false}]}";
        var payload = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = inner } } }, usage = new { prompt_tokens = 300, completion_tokens = 100 } });
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) }));
        var ledger = new BudgetLedger(LedgerPath()); var client = new DeepSeekClient(new HttpClient(handler), ledger, () => "fake");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.TranslateImageAsync(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, "duplicate"));
        Assert.Equal("complete", Assert.Single(ledger.Snapshot().Entries).Status);
    }
    [Fact]
    public async Task ImagePromptRequiresFaithfulNeutralHeadings()
    {
        string? requestBody = null;
        const string inner = "{\"groups\":[{\"id\":\"1\",\"source\":\"LAB NOTES\",\"translation\":\"实验室笔记\",\"uncertain\":false}]}";
        var payload = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = inner } } }, usage = new { prompt_tokens = 300, completion_tokens = 100 } });
        var handler = new Handler(async (request, token) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
        });
        var client = new DeepSeekClient(new HttpClient(handler), new BudgetLedger(LedgerPath()), () => "fake-key");
        var result = await client.TranslateImageAsync(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, "heading");
        Assert.Contains("do not turn neutral notes", requestBody);
        Assert.Equal("LAB NOTES", Assert.Single(result.Groups).Source);
        Assert.Equal("实验室笔记", result.Groups[0].Translation);
    }
    [Fact]
    public async Task InvalidKeyIsRejectedWithoutExposingItOrReservingBudget()
    {
        const string badKey = "secret\nleak";
        var handler = new Handler((_, _) => throw new Exception("HTTP must not be called"));
        var ledger = new BudgetLedger(LedgerPath());
        var client = new DeepSeekClient(new HttpClient(handler), ledger, () => badKey);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync("test", true, "key-test"));
        Assert.Equal("API Key 格式无效，请重新设置。", exception.Message);
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.Empty(ledger.Snapshot().Entries);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
