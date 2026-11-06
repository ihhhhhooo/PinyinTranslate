using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using PinyinTranslate.App;
using PinyinTranslate.Core;
using PinyinTranslate.Selection;

namespace PinyinTranslate.Screenshot.Tests;

// Exercises the actual app translation/validation flow without constructing
// DesktopServices, a dispatcher, UIA, hooks, clipboard or any result window.
public sealed class SelectionTranslationFlowTests
{
    [Fact]
    public async Task A_read_only_document_selection_reaches_translation_and_presentation()
    {
        using var fixture = new Fixture();
        var source = Snapshot(1, "Document body selected by the user.");
        await fixture.Run(source);
        Assert.Equal(source.Text, Assert.Single(fixture.Translated));
        var shown = Assert.Single(fixture.Shown);
        Assert.Equal(source, shown.Source);
        Assert.Equal("译文：" + source.Text, shown.Translation);
        Assert.Equal(2, fixture.Monitor.Validations);
        Assert.Null(fixture.Get("_dispatcher"));
        Assert.Null(fixture.Get("_client"));
        Assert.Null(fixture.Get("_resultWindow"));
        Assert.Contains("翻译完成", fixture.Services.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failed_validation_before_or_after_translation_never_presents_a_result(int rejectedValidation)
    {
        using var fixture = new Fixture();
        fixture.Monitor.Validation = (_, count) => count != rejectedValidation;
        await fixture.Run(Snapshot(1, "Selected web page text"));
        Assert.Empty(fixture.Shown);
        Assert.Equal(rejectedValidation == 1 ? 0 : 1, fixture.Translated.Count);
        Assert.Contains("本轮已结束", fixture.Services.Status);
    }

    [Fact]
    public async Task A_cleared_selection_cancels_the_request_without_presenting_the_late_response()
    {
        using var fixture = new Fixture();
        var started = Completion<bool>();
        var canceled = Completion<bool>();
        var response = Completion<string>();
        fixture.Translate = (_, token) =>
        {
            token.Register(() => canceled.TrySetResult(true));
            started.TrySetResult(true);
            return response.Task;
        };
        Task translating = fixture.Run(Snapshot(1, "Old selected text"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Invalidate(2);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        response.SetResult("旧译文");
        await translating.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Shown);
        Assert.Equal(2, Assert.Single(fixture.Dismissed));
    }

    [Fact]
    public async Task A_new_selection_wins_over_a_canceled_older_request()
    {
        using var fixture = new Fixture();
        var started = Completion<bool>();
        var response = Completion<string>();
        fixture.Translate = (text, _) => text == "first" ? WaitForOld() : Task.FromResult("第二段译文");
        Task<string> WaitForOld() { started.TrySetResult(true); return response.Task; }
        Task old = fixture.Run(Snapshot(1, "first"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Invalidate(2);
        await fixture.Run(Snapshot(2, "second"));
        response.SetResult("不应显示的第一段译文");
        await old.WaitAsync(TimeSpan.FromSeconds(5));
        var shown = Assert.Single(fixture.Shown);
        Assert.Equal("second", shown.Source.Text);
        Assert.Equal("第二段译文", shown.Translation);
    }

    [Fact]
    public async Task A_queued_presentation_rechecks_scope_after_selection_changes()
    {
        using var fixture = new Fixture();
        var queued = Completion<bool>();
        var release = Completion<bool>();
        fixture.Present = async (source, text, token, current) =>
        {
            queued.TrySetResult(true);
            await release.Task;
            if (current()) fixture.Shown.Enqueue((source, text));
        };
        Task request = fixture.Run(Snapshot(1, "A highlighted paragraph"));
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Invalidate(2);
        release.SetResult(true);
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Shown);
    }

    [Fact]
    public async Task Disabled_or_stale_snapshots_do_not_start_a_translation()
    {
        using var fixture = new Fixture();
        fixture.Monitor.Enabled = false;
        await fixture.Run(Snapshot(1, "A stale selection"));
        Assert.Empty(fixture.Translated);
        Assert.Empty(fixture.Shown);
    }

    [Fact]
    public async Task A_current_translation_error_is_reported_without_opening_a_window()
    {
        using var fixture = new Fixture();
        fixture.Translate = (_, _) => Task.FromException<string>(new InvalidOperationException("Synthetic API failure"));
        await fixture.Run(Snapshot(1, "Some selected text"));
        Assert.Empty(fixture.Shown);
        Assert.Contains("Synthetic API failure", fixture.Services.Status);
    }

    [Theory]
    [InlineData("选中中文进行翻译", false)]
    [InlineData("請翻譯這段文字", false)]
    [InlineData("Selected English text", true)]
    [InlineData("我使用 DeepSeek API 翻译", false)]
    public async Task TheAppPassesTheSelectedPairAndUsesTheProviderReportedTarget(string text, bool targetChinese)
    {
        using var fixture = new Fixture();
        fixture.Target = targetChinese ? TranslationLanguages.SimplifiedChinese : TranslationLanguages.English;
        await fixture.Run(Snapshot(1, text));
        Assert.Equal((TranslationLanguages.SimplifiedChinese, TranslationLanguages.English), Assert.Single(fixture.Pairs));
        Assert.Single(fixture.Shown);
        Assert.Equal(fixture.Target, Assert.Single(fixture.ShownTargets));
        Assert.Contains(TranslationLanguages.Get(fixture.Target).DisplayName, fixture.Services.Status);
    }

    [Fact]
    public async Task TheAppUsesTheTargetDirectionInItsCacheAndReusesOnlyTheMatchingTranslation()
    {
        using var fixture = new Fixture();
        const string source = "中文原文";
        var cache = Assert.IsType<TranslationCache>(fixture.Get("_cache"));
        await cache.GetOrTranslateAsync(DeepSeekClient.Model, "en-zh", "selection-v2", source,
            _ => Task.FromResult("错误方向的旧缓存"));
        fixture.Translate = (_, _) => Task.FromResult("English translation");
        fixture.Target = TranslationLanguages.English;
        await fixture.Run(Snapshot(1, source));
        await fixture.Run(Snapshot(2, source));
        Assert.Single(fixture.Translated);
        Assert.Equal(2, fixture.Shown.Count);
        Assert.All(fixture.Shown, result => Assert.Equal("English translation", result.Translation));
    }

    [Fact]
    public async Task ATimeoutEndsEvenANonCooperativeRequestAndItsLateReplyCannotReplaceTheNextResult()
    {
        using var fixture = new Fixture();
        fixture.Set("_selectionTranslationTimeout", TimeSpan.FromMilliseconds(100));
        var late = Completion<string>();
        var canceled = Completion<bool>();
        fixture.Translate = (text, token) =>
        {
            if (text != "slow") return Task.FromResult("新的译文");
            token.Register(() => canceled.TrySetResult(true));
            return late.Task;
        };
        await fixture.Run(Snapshot(1, "slow")).WaitAsync(TimeSpan.FromSeconds(3));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("翻译超时", fixture.Services.Status);
        Assert.Empty(fixture.Shown);
        await fixture.Run(Snapshot(2, "new selection"));
        string status = fixture.Services.Status;
        late.SetResult("旧结果");
        Assert.Equal(status, fixture.Services.Status);
        Assert.Equal("新的译文", Assert.Single(fixture.Shown).Translation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ASlowSelectionValidationBeforeOrAfterTheApiHasABoundedWait(int stage)
    {
        using var fixture = new Fixture();
        fixture.Set("_selectionValidationTimeout", TimeSpan.FromMilliseconds(100));
        var late = Completion<bool>();
        fixture.Monitor.AsyncValidation = (_, count) => count == stage ? late.Task : Task.FromResult(true);
        await fixture.Run(Snapshot(1, "A slow page")).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("选区确认超时", fixture.Services.Status);
        Assert.Empty(fixture.Shown);
        Assert.Equal(stage == 1 ? 0 : 1, fixture.Translated.Count);
        fixture.Monitor.AsyncValidation = null;
        await fixture.Run(Snapshot(2, "A fresh page selection"));
        string status = fixture.Services.Status;
        late.SetResult(true);
        Assert.Equal(status, fixture.Services.Status);
        Assert.Equal("A fresh page selection", Assert.Single(fixture.Shown).Source.Text);
    }

    [Fact]
    public async Task CancelingAnOldSelectionClearsItsWaitingStatusWithoutWaitingForTheProvider()
    {
        using var fixture = new Fixture();
        var entered = Completion<bool>();
        var late = Completion<string>();
        fixture.Translate = (_, _) => { entered.TrySetResult(true); return late.Task; };
        Task request = fixture.Run(Snapshot(1, "An old selection"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Invalidate(2);
        Assert.Contains("已取消", fixture.Services.Status);
        await request.WaitAsync(TimeSpan.FromSeconds(3));
        late.SetResult("Old result");
        Assert.Empty(fixture.Shown);
    }

    [Fact]
    public async Task AnUnrelatedNewerStatusIsNotOverwrittenByTheOldSelectionResponse()
    {
        using var fixture = new Fixture();
        var entered = Completion<bool>();
        var late = Completion<string>();
        fixture.Translate = (_, _) => { entered.TrySetResult(true); return late.Task; };
        Task request = fixture.Run(Snapshot(1, "Selected text"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.OtherStatus("输入模式已关闭。");
        late.SetResult("译文");
        await request;
        Assert.Equal("输入模式已关闭。", fixture.Services.Status);
    }

    [Fact]
    public async Task ASlowCancellationCallbackDoesNotHoldTheCompletedSelectionTaskOpen()
    {
        using var fixture = new Fixture();
        var entered = Completion<bool>();
        var release = Completion<bool>();
        fixture.RequestCreated = token => token.Register(() =>
        { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); });
        try
        {
            await fixture.Run(Snapshot(1, "selected text")).WaitAsync(TimeSpan.FromSeconds(3));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("翻译完成", fixture.Services.Status);
        }
        finally { release.TrySetResult(true); }
    }

    [Theory]
    [InlineData("en", "ko", "ko", "Hello")]
    [InlineData("en", "ko", "en", "안녕하세요")]
    [InlineData("fr", "de", "de", "Bonjour")]
    [InlineData("ja", "vi", "vi", "こんにちは")]
    [InlineData("zh-Hans", "zh-Hant", "zh-Hant", "软件翻译")]
    public async Task AnySelectedPairReachesTheProviderAndTheActualTargetReachesPresentation(string a, string b, string target, string source)
    {
        using var fixture = new Fixture();
        fixture.ChangePair(a, b);
        fixture.Target = target;
        await fixture.Run(Snapshot(10, source));
        Assert.Equal((a, b), Assert.Single(fixture.Pairs));
        Assert.Equal(source, Assert.Single(fixture.Translated));
        Assert.Equal(target, Assert.Single(fixture.ShownTargets));
        Assert.Contains(TranslationLanguages.Get(target).DisplayName, fixture.Services.Status);
    }

    [Fact]
    public async Task PairAndChineseScriptChangesCannotReuseEachOthersCacheEntries()
    {
        using var fixture = new Fixture();
        const string source = "Same selected text";
        fixture.Translate = (_, _) => Task.FromResult("软件");
        await fixture.Run(Snapshot(1, source));
        fixture.ChangePair("zh-Hant", "en");
        fixture.Target = "zh-Hant";
        fixture.Translate = (_, _) => Task.FromResult("軟體");
        await fixture.Run(Snapshot(2, source));
        fixture.ChangePair("zh-Hans", "en");
        fixture.Target = "zh-Hans";
        fixture.Translate = (_, _) => throw new Exception("The original pair should use its cache");
        await fixture.Run(Snapshot(3, source));
        Assert.Equal(2, fixture.Translated.Count);
        Assert.Equal(new[] { "软件", "軟體", "软件" }, fixture.Shown.Select(x => x.Translation));
        Assert.Equal(new[] { "zh-Hans", "zh-Hant", "zh-Hans" }, fixture.ShownTargets);
    }

    [Fact]
    public async Task ReversingThePairUsesADifferentCacheBecauseTheFallbackLanguageChanges()
    {
        using var fixture = new Fixture();
        fixture.Target = "en";
        await fixture.Run(Snapshot(1, "Ambiguous source"));
        fixture.ChangePair("en", "zh-Hans");
        await fixture.Run(Snapshot(2, "Ambiguous source"));
        Assert.Equal(2, fixture.Translated.Count);
        Assert.Equal(new[] { ("zh-Hans", "en"), ("en", "zh-Hans") }, fixture.Pairs);
    }

    [Fact]
    public async Task ChangingThePairCancelsAnOldInFlightResultAndLeavesTheNewResultIntact()
    {
        using var fixture = new Fixture();
        var started = Completion<bool>();
        var canceled = Completion<bool>();
        var late = Completion<string>();
        fixture.Translate = (_, token) =>
        {
            token.Register(() => canceled.TrySetResult(true));
            started.TrySetResult(true); return late.Task;
        };
        Task old = fixture.Run(Snapshot(1, "old source"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.ChangePair("en", "ko");
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await old.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Target = "ko";
        fixture.Translate = (_, _) => Task.FromResult("새로운 번역");
        await fixture.Run(Snapshot(10, "new source"));
        string status = fixture.Services.Status;
        late.TrySetResult("Old translation");
        Assert.Equal("새로운 번역", Assert.Single(fixture.Shown).Translation);
        Assert.Equal("ko", Assert.Single(fixture.ShownTargets));
        Assert.Equal(status, fixture.Services.Status);
    }

    [Fact]
    public async Task AnOutsidePairTargetIsNeverPresentedOrRetainedInTheCache()
    {
        using var fixture = new Fixture();
        fixture.Target = "ja";
        await fixture.Run(Snapshot(1, "same source"));
        Assert.Empty(fixture.Shown);
        Assert.Contains("不属于所选语言对", fixture.Services.Status);
        fixture.Target = "en";
        await fixture.Run(Snapshot(2, "same source"));
        Assert.Equal(2, fixture.Translated.Count);
        Assert.Single(fixture.Shown);
    }

    private static SelectionSnapshot Snapshot(long version, string text) => new(version, 1234, 42, "document-body", text);
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly TranslationCache _cache = new();
        private readonly List<CancellationTokenSource> _requests = [];
        public DesktopServices Services { get; } = (DesktopServices)RuntimeHelpers.GetUninitializedObject(typeof(DesktopServices));
        public FakeMonitor Monitor { get; } = new();
        public ConcurrentQueue<string> Translated { get; } = new();
        public ConcurrentQueue<(string A, string B)> Pairs { get; } = new();
        public ConcurrentQueue<string> ShownTargets { get; } = new();
        public ConcurrentQueue<(SelectionSnapshot Source, string Translation)> Shown { get; } = new();
        public ConcurrentQueue<long> Dismissed { get; } = new();
        public Func<string, CancellationToken, Task<string>> Translate { get; set; } = (text, _) => Task.FromResult("译文：" + text);
        public string Target { get; set; } = TranslationLanguages.SimplifiedChinese;
        public Action<CancellationToken>? RequestCreated { get; set; }
        public Func<SelectionSnapshot, string, CancellationToken, Func<bool>, Task> Present { get; set; }

        public Fixture()
        {
            Present = (source, text, _, current) => { if (current()) Shown.Enqueue((source, text)); return Task.CompletedTask; };
            Set("_gate", new object());
            Set("_selection", Monitor);
            Set("_cache", _cache);
            Set("_status", "Ready");
            Set("<Settings>k__BackingField", new AppSettings("Ctrl+Alt+T", "Ctrl+Alt+D", "Ctrl+Alt+S"));
            Set("<SelectionEnabled>k__BackingField", true);
            Set("_selectionTranslator", new Func<string, string, string, CancellationToken, Task<TextTranslation>>(async (text, a, b, token) =>
            {
                Translated.Enqueue(text); Pairs.Enqueue((a, b));
                string target = Target;
                return new(await Translate(text, token), target);
            }));
            Set("_presentSelection", new Func<SelectionSnapshot, TextTranslation, CancellationToken, Func<bool>, Task>(
                (source, text, token, current) =>
                {
                    if (current()) ShownTargets.Enqueue(text.TargetLanguage);
                    return Present(source, text.Translation, token, current);
                }));
            Set("_dismissSelection", new Action<long>(Dismissed.Enqueue));
        }

        public Task Run(SelectionSnapshot source)
        {
            Monitor.Version = source.Version;
            var cancellation = new CancellationTokenSource();
            RequestCreated?.Invoke(cancellation.Token);
            _requests.Add(cancellation);
            Set("_selectionRequest", cancellation);
            Set("_selectionRequestVersion", source.Version);
            return (Task)typeof(DesktopServices).GetMethod("TranslateSelectionAsync", PrivateInstance)!.Invoke(Services, [source, cancellation])!;
        }

        public void Invalidate(long version)
        {
            Monitor.Version = version;
            typeof(DesktopServices).GetMethod("SelectionInvalidated", PrivateInstance)!.Invoke(Services, [version]);
        }
        public void ChangePair(string a, string b) => typeof(DesktopServices).GetMethod("CommitSettings", PrivateInstance)!.Invoke(Services,
            [Services.Settings with { SelectionLanguageA = a, SelectionLanguageB = b }]);
        public object? Get(string name) => typeof(DesktopServices).GetField(name, PrivateInstance)!.GetValue(Services);
        public void Set(string name, object? value) => typeof(DesktopServices).GetField(name, PrivateInstance)!.SetValue(Services, value);
        public void OtherStatus(string message) => typeof(DesktopServices).GetMethod("SetStatus", PrivateInstance)!.Invoke(Services, [message]);
        public void Dispose()
        {
            Set("_disposed", 1);
            Monitor.Dispose();
            foreach (var request in _requests) { try { request.Cancel(); } catch (ObjectDisposedException) { } }
            _cache.Dispose();
        }
    }

    private sealed class FakeMonitor : ISelectionMonitor
    {
        public bool Enabled { get; set; } = true;
        public long Version { get; set; }
        public int Validations;
        public Func<SelectionSnapshot, int, bool> Validation { get; set; } = (_, _) => true;
        public Func<SelectionSnapshot, int, Task<bool>>? AsyncValidation { get; set; }
        public Task<bool> Ready => Task.FromResult(true);
        public bool IsCurrent(long version) => Enabled && version == Version;
        public Task<bool> ValidateAsync(SelectionSnapshot source)
        {
            if (!IsCurrent(source.Version)) return Task.FromResult(false);
            int count = Interlocked.Increment(ref Validations);
            return AsyncValidation?.Invoke(source, count) ?? Task.FromResult(Validation(source, count));
        }
        public void SetEnabled(bool value, bool requireFreshSelection = false) { Enabled = value; Version++; }
        public void Dispose() => Enabled = false;
        public event Action<long>? Invalidated { add { } remove { } }
        public event Action<SelectionSnapshot>? SelectionChanged { add { } remove { } }
        public event Action<string>? StatusChanged { add { } remove { } }
    }
}
