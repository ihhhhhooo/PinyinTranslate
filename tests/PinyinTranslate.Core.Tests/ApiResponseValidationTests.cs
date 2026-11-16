using System.Net;
using PinyinTranslate.Core;

namespace PinyinTranslate.Core.Tests;

// Pure fake HTTP; no production key, network or desktop services.
public sealed class ApiResponseValidationTests
{
    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":null}")]
    [InlineData("{\"choices\":{}}")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":null}]}")]
    public async Task Missing_choices_are_reported_as_invalid_service_results(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body) })));
        var ledger = new Ledger(); var client = new DeepSeekClient(http, ledger, () => "fake-key");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.TranslateAsync("source", "en", "invalid-envelope"));
        Assert.Contains("无效或不完整", error.Message); Assert.Equal(1, ledger.Requests); Assert.Equal(1, ledger.Unknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_transport_timeout_is_distinct_from_user_cancellation_and_is_not_retried(bool image)
    {
        using var http = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("synthetic transport timeout")));
        var ledger = new Ledger(); var client = new DeepSeekClient(http, ledger, () => "fake-key");
        Func<Task> action = image ? () => client.TranslateImageAsync([137, 80, 78, 71, 13, 10, 26, 10], "timeout") :
            () => client.TranslateAsync("source", "en", "timeout");
        var error = await Assert.ThrowsAsync<TimeoutException>(action);
        Assert.Contains("超时", error.Message); Assert.Equal(1, ledger.Requests); Assert.Equal(1, ledger.Unknown);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class Ledger : IApiRequestLedger
    {
        public int Requests, Unknown;
        public Guid Reserve(string caseName, int maxInputTokens, int maxOutputTokens) { Requests++; return Guid.NewGuid(); }
        public void Complete(Guid id, int inputTokens, int outputTokens) { }
        public void MarkUnknown(Guid id) => Unknown++;
    }
}
