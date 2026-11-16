using System.Net;
using System.Text.Json;
using PinyinTranslate.Core;

namespace PinyinTranslate.Core.Tests;

// Synthetic HTTP replies only. No screenshots, network, credentials or UI are used.
public sealed class ImageTranslationTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10];
    private const string Group = "{\"id\":\"1\",\"source\":\"Visible text\",\"translation\":\"可见文字\",\"uncertain\":false}";
    private const string Valid = "{\"groups\":[" + Group + "]}";

    [Fact]
    public async Task Missing_groups_are_recovered_by_one_new_read_of_the_same_image_and_target()
    {
        using var fixture = new Fixture("{\"message\":\"nonconforming result\"}", Valid);
        var result = await fixture.Client.TranslateImageAsync(Png, "zh-Hant", "synthetic-image");
        Assert.Equal("Visible text", Assert.Single(result.Groups).Source);
        Assert.Equal(2, fixture.Bodies.Count); Assert.Equal(2, fixture.Ledger.Completed);
        using var first = JsonDocument.Parse(fixture.Bodies[0]); using var second = JsonDocument.Parse(fixture.Bodies[1]);
        var messages = second.RootElement.GetProperty("messages");
        Assert.Equal(first.RootElement.GetProperty("messages")[1].GetProperty("content")[1].GetRawText(),
            messages[1].GetProperty("content")[1].GetRawText());
        Assert.Contains("(zh-Hant)", messages[0].GetProperty("content").GetString());
        Assert.NotEqual(first.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(),
            messages[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("```json\n" + Valid + "\n```")]
    [InlineData("\uFEFF  " + Valid)]
    [InlineData("[" + Group + "]")]
    [InlineData(Group)]
    public async Task Equivalent_json_shapes_are_normalized_without_another_api_call(string content)
    {
        using var fixture = new Fixture(content);
        var result = await fixture.Client.TranslateImageAsync(Png, "normalized");
        var group = Assert.Single(result.Groups);
        Assert.Equal("1", group.Id); Assert.Equal("Visible text", group.Source); Assert.Equal("可见文字", group.Translation);
        Assert.True(result.NeedsReview); Assert.Single(fixture.Bodies);
    }

    [Fact]
    public async Task Omitted_display_ids_and_uncertainty_do_not_discard_complete_text_pairs()
    {
        using var fixture = new Fixture("{\"Groups\":[{\"Source\":\" first \",\"Translation\":\" 第一 \",\"Id\":1}," +
            "{\"source\":\"second\",\"translation\":\"第二\"}]}");
        var result = await fixture.Client.TranslateImageAsync(Png, "complete-pairs");
        Assert.Equal(new[] { "1", "2" }, result.Groups.Select(x => x.Id));
        Assert.Equal(" first ", result.Groups[0].Source); Assert.Equal(" 第一 ", result.Groups[0].Translation);
        Assert.All(result.Groups, group => Assert.True(group.Uncertain)); Assert.Single(fixture.Bodies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("{\"message\":\"missing groups\"}")]
    [InlineData("{\"groups\":{}}")]
    [InlineData("{\"groups\":[null]}")]
    [InlineData("{\"groups\":[" + Group + "," + Group + "]}")]
    [InlineData("{\"groups\":[{\"id\":\"2\",\"source\":\"source\",\"translation\":\"译文\"}]}")]
    [InlineData("{\"groups\":[{\"source\":\"source\",\"translation\":\" \"}]}")]
    [InlineData("{\"groups\":[{\"source\":\"source\",\"Source\":\"conflict\",\"translation\":\"译文\"}]}")]
    [InlineData("{\"groups\":[{\"source\":\"source\",\"translation\":\"译文\",\"uncertain\":\"false\"}]}")]
    [InlineData("{\"groups\":[" + Group + "],\"Groups\":[]}")]
    [InlineData("Explanation before JSON: " + Valid)]
    public async Task Corrupt_or_ambiguous_results_are_never_partially_accepted_and_retry_only_once(string content)
    {
        using var fixture = new Fixture(content);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.TranslateImageAsync(Png, "invalid-image-result"));
        Assert.Contains("自动重新识别一次", error.Message); Assert.DoesNotContain("缺少对照组", error.Message);
        Assert.Equal(2, fixture.Bodies.Count); Assert.Equal(2, fixture.Ledger.Reserved); Assert.Equal(2, fixture.Ledger.Completed);
        Assert.Equal(0, fixture.Ledger.Unknown);
    }

    [Theory]
    [InlineData("{\"groups\":[]}")]
    [InlineData("[]")]
    public async Task No_visible_text_has_a_clear_terminal_result_without_another_request(string content)
    {
        using var fixture = new Fixture(content);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.TranslateImageAsync(Png, "no-text"));
        Assert.Contains("未识别到可翻译文本", error.Message); Assert.Single(fixture.Bodies);
    }

    [Fact]
    public async Task Cancelling_after_the_first_response_prevents_a_format_retry()
    {
        using var fixture = new Fixture("{}", Valid); using var cancellation = new CancellationTokenSource();
        fixture.Ledger.OnComplete = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.TranslateImageAsync(Png, "cancel-image", cancellation.Token));
        Assert.Single(fixture.Bodies); Assert.Equal(1, fixture.Ledger.Completed);
    }

    [Fact]
    public async Task Cancellation_during_the_second_request_stops_with_both_attempts_accounted()
    {
        using var fixture = new Fixture("{}", Valid); using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeReply = async (index, token) =>
        { if (index == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        var request = fixture.Client.TranslateImageAsync(Png, "cancel-retry", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(2, fixture.Bodies.Count); Assert.Equal(2, fixture.Ledger.Reserved);
        Assert.Equal(1, fixture.Ledger.Completed); Assert.Equal(1, fixture.Ledger.Unknown);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Http_errors_do_not_trigger_format_retries(HttpStatusCode status)
    {
        using var fixture = new Fixture(Valid); fixture.StatusCode = status;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.TranslateImageAsync(Png, "http-error"));
        Assert.Single(fixture.Bodies); Assert.Equal(1, fixture.Ledger.Unknown);
    }

    [Fact]
    public async Task A_truncated_response_is_not_parsed_or_silently_returned_as_a_partial_translation()
    {
        using var fixture = new Fixture(Valid); fixture.FinishReason = "length";
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.TranslateImageAsync(Png, "truncated-image"));
        Assert.Contains("截断", error.Message); Assert.Single(fixture.Bodies); Assert.Equal(1, fixture.Ledger.Completed);
    }

    [Fact]
    public async Task Missing_usage_on_the_first_attempt_is_retained_when_the_retry_succeeds()
    {
        using var fixture = new Fixture("{}", Valid);
        fixture.BeforeReply = (index, _) => { fixture.IncludeUsage = index != 0; return Task.CompletedTask; };
        Assert.Single((await fixture.Client.TranslateImageAsync(Png, "missing-usage")).Groups);
        Assert.Equal(2, fixture.Ledger.Reserved); Assert.Equal(1, fixture.Ledger.Completed); Assert.Equal(1, fixture.Ledger.Unknown);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient http;
        public List<string> Bodies { get; } = [];
        public FakeLedger Ledger { get; } = new();
        public DeepSeekClient Client { get; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string FinishReason { get; set; } = "stop";
        public bool IncludeUsage { get; set; } = true;
        public Func<int, CancellationToken, Task>? BeforeReply { get; set; }
        public Fixture(params string[] responses)
        {
            http = new HttpClient(new Handler(async (request, token) =>
            {
                int index = Bodies.Count; Bodies.Add(await request.Content!.ReadAsStringAsync(token));
                if (BeforeReply is not null) await BeforeReply(index, token);
                string content = responses[Math.Min(index, responses.Length - 1)];
                var body = new Dictionary<string, object> { ["choices"] = new[] { new { finish_reason = FinishReason, message = new { content } } } };
                if (IncludeUsage) body["usage"] = new { prompt_tokens = 200, completion_tokens = 40 };
                return new HttpResponseMessage(StatusCode) { Content = new StringContent(JsonSerializer.Serialize(body)) };
            }));
            Client = new(http, Ledger, () => "fake-test-key");
        }
        public void Dispose() => http.Dispose();
    }
    private sealed class FakeLedger : IApiRequestLedger
    {
        public int Reserved, Completed, Unknown;
        public Action? OnComplete;
        public Guid Reserve(string caseName, int maxInputTokens, int maxOutputTokens) { Reserved++; return Guid.NewGuid(); }
        public void Complete(Guid id, int inputTokens, int outputTokens) { Completed++; OnComplete?.Invoke(); }
        public void MarkUnknown(Guid id) => Unknown++;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
