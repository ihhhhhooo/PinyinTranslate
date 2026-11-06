using System.Net;
using System.Text;
using System.Text.Json;
using PinyinTranslate.Core;

namespace PinyinTranslate.Core.Tests;

public sealed class SelectionTranslationLanguageTests
{
    [Theory]
    [InlineData("属于", false)]
    [InlineData("請將這段文字翻譯成英文。", false)]
    [InlineData("𠮷", false)]
    [InlineData("It seems to be better already.", true)]
    [InlineData("Hello", true)]
    [InlineData("我使用 DeepSeek API 翻译这段文字。", false)]
    [InlineData("These labels include 中文 for bilingual readers.", true)]
    [InlineData("请看 https://example.com/a/very/long/english/path", false)]
    [InlineData("Use `中文` as a variable name in this example.", true)]
    [InlineData("解释 `some long English code goes here` 的含义", false)]
    [InlineData("Windows版", false)]
    [InlineData("日本語の文章です", true)]
    [InlineData("한국어 문장", true)]
    [InlineData("12345 🙂", true)]
    public void LocalDirectionHandlesChineseEnglishAndMixedProse(string source, bool toChinese)
        => Assert.Equal(toChinese, SelectionTranslationLanguage.ShouldTranslateToChinese(source));

    [Theory]
    [InlineData("选中中文翻译成英文。", false)]
    [InlineData("Translate selected English into Chinese.", true)]
    public async Task ResolvedDirectionReachesTheRealClientRequestWithoutModifyingTheSource(string source, bool toChinese)
    {
        string? sent = null, prompt = null;
        var ledger = new Ledger();
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            sent = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"mock translation\"}}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":3}}", Encoding.UTF8, "application/json") };
        }));
        var client = new DeepSeekClient(http, ledger, () => "fake-test-key");
        Assert.Equal("mock translation", await client.TranslateAsync(source,
            SelectionTranslationLanguage.ShouldTranslateToChinese(source), "selection-fake"));
        Assert.Equal(source, sent);
        Assert.Contains(toChinese ? "Simplified Chinese (zh-Hans)" : "English (en)", prompt);
        Assert.Equal(1, ledger.Requests); Assert.Equal(1, ledger.Completed);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class Ledger : IApiRequestLedger
    {
        public int Requests, Completed;
        public Guid Reserve(string caseName, int maxInputTokens, int maxOutputTokens) { Requests++; return Guid.NewGuid(); }
        public void Complete(Guid id, int inputTokens, int outputTokens) => Completed++;
        public void MarkUnknown(Guid id) { }
    }
}
