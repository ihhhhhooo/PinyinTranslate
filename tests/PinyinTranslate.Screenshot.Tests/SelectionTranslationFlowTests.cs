using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using PinyinTranslate.App;
using PinyinTranslate.Core;
using PinyinTranslate.Input;
using PinyinTranslate.Selection;

namespace PinyinTranslate.Screenshot.Tests;

// Exercises the actual app translation/validation flow without constructing
// DesktopServices, a dispatcher, UIA, hooks, clipboard or any result window.
public sealed class SelectionTranslationFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_only_document_selection_reaches_translation_and_presentation(bool copiedText)
    {
        using var fixture = new Fixture();
        var source = Snapshot(1, "Document body selected by the user.") with { IsCopiedText = copiedText };
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
        Assert.Equal(shown.Translation, Assert.Single(fixture.Clipboard.Copies));
        Assert.Contains("已复制译文", Assert.Single(fixture.CopyFeedback));
        if (copiedText) Assert.Contains("复制兼容", fixture.Services.Status);
    }

    [Fact]
    public async Task Manual_copy_and_accessible_selection_share_the_same_translation_cache()
    {
        using var fixture = new Fixture();
        await fixture.Run(Snapshot(1, "Same copied and selected text"));
        await fixture.Run(Snapshot(2, "Same copied and selected text") with { IsCopiedText = true });
        Assert.Single(fixture.Translated); Assert.Equal(2, fixture.Shown.Count);
        Assert.True(fixture.Shown.Last().Source.IsCopiedText); Assert.Contains("复制兼容", fixture.Services.Status);
        Assert.Equal(2, fixture.Clipboard.Copies.Count);
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
        Assert.Empty(fixture.Clipboard.Copies);
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
        Assert.Empty(fixture.Clipboard.Copies);
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
        Assert.Empty(fixture.Clipboard.Copies);
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

    [Fact]
    public async Task AutoCopyCanBeDisabledWithoutDisablingTranslationAndEnabledForTheNextSelection()
    {
        using var fixture = new Fixture();
        fixture.SetAutoCopy(false);
        await fixture.Run(Snapshot(1, "Translate without replacing the clipboard"));
        Assert.Single(fixture.Shown);
        Assert.Equal(0, fixture.Clipboard.Attempts);
        Assert.Contains("点击译文可复制", Assert.Single(fixture.CopyFeedback));
        fixture.SetAutoCopy(true);
        await fixture.Run(Snapshot(2, "A newly selected sentence"));
        Assert.Equal("译文：A newly selected sentence", Assert.Single(fixture.Clipboard.Copies));
        Assert.Contains("已复制译文", fixture.CopyFeedback.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingAutoCopyWhileTranslatingDoesNotRetroactivelyAuthorizeThatRequest(bool initiallyEnabled)
    {
        using var fixture = new Fixture();
        fixture.SetAutoCopy(initiallyEnabled);
        var entered = Completion<bool>();
        var release = Completion<string>();
        fixture.Translate = (_, _) => { entered.TrySetResult(true); return release.Task; };
        Task request = fixture.Run(Snapshot(1, "The earlier selection"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.SetAutoCopy(!initiallyEnabled);
        if (initiallyEnabled) fixture.SetAutoCopy(true);
        release.SetResult("Show this result without copying it");
        await request.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(fixture.Shown);
        Assert.Empty(fixture.Clipboard.Copies);
        Assert.Equal(0, fixture.Clipboard.Attempts);
        Assert.DoesNotContain("已复制译文", fixture.Services.Status);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("mode")]
    [InlineData("screenshot")]
    [InlineData("dispose")]
    [InlineData("languages")]
    [InlineData("update")]
    [InlineData("limit")]
    public async Task AQueuedCopyRechecksAllRevocationBarriersBeforeTouchingTheClipboard(string change)
    {
        using var fixture = new Fixture();
        var entered = Completion<bool>();
        var release = Completion<bool>();
        var finished = Completion<bool>();
        fixture.Clipboard.Copy = async (text, current, token) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            var result = fixture.Clipboard.Publish(text, current, token);
            finished.TrySetResult(true);
            return result;
        };
        Task request = fixture.Run(Snapshot(1, "The queued source"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        switch (change)
        {
            case "selection": fixture.Invalidate(2); break;
            case "mode": fixture.Set("<SelectionEnabled>k__BackingField", false); break;
            case "screenshot": fixture.Set("_captureSuspendCount", 1); break;
            case "dispose": fixture.Set("_disposed", 1); break;
            case "languages": fixture.ChangePair("en", "ja"); break;
            case "limit": fixture.ChangeLimit(1); break;
            case "update":
                var preparation = new UpdatePreparation(() => new(false, true), _ => Task.CompletedTask, _ => Task.CompletedTask);
                fixture.Set("_updatePreparation", preparation);
                await preparation.PrepareAsync(default);
                break;
        }
        release.TrySetResult(true);
        await request.WaitAsync(TimeSpan.FromSeconds(3));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(fixture.Clipboard.Copies);
        Assert.Empty(fixture.Shown);
    }

    [Fact]
    public async Task DisablingAndReenablingAutoCopyRevokesAnAlreadyQueuedWriteButKeepsItsPreview()
    {
        using var fixture = new Fixture();
        var entered = Completion<bool>();
        var release = Completion<bool>();
        fixture.Clipboard.Copy = async (text, current, token) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return fixture.Clipboard.Publish(text, current, token);
        };
        Task request = fixture.Run(Snapshot(1, "A queued copy"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.SetAutoCopy(false);
        fixture.SetAutoCopy(true);
        release.SetResult(true);
        await request.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(fixture.Clipboard.Copies);
        Assert.Single(fixture.Shown);
        Assert.Contains("点击译文可复制", Assert.Single(fixture.CopyFeedback));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClipboardFailureStillPresentsTheTranslationAndNeverReportsACopySuccess(bool throws)
    {
        using var fixture = new Fixture();
        fixture.Clipboard.Copy = (_, _, _) => throws
            ? throw new InvalidOperationException("Synthetic clipboard failure")
            : Task.FromResult(new TranslationClipboardCopyResult(false, false, "ClipboardBusy"));
        await fixture.Run(Snapshot(1, "A successfully translated selection"));
        Assert.Single(fixture.Shown);
        Assert.Empty(fixture.Clipboard.Copies);
        Assert.Contains("翻译完成", fixture.Services.Status);
        Assert.Contains("点击译文可复制", Assert.Single(fixture.CopyFeedback));
        Assert.DoesNotContain("已复制译文", fixture.Services.Status);
    }

    [Fact]
    public async Task ABlockedClipboardHasABoundedWaitAndItsLateCopyCannotOverwriteANewerResult()
    {
        using var fixture = new Fixture();
        fixture.Set("_selectionCopyTimeout", TimeSpan.FromMilliseconds(50));
        var release = Completion<bool>();
        var finished = Completion<bool>();
        fixture.Clipboard.Copy = async (text, current, token) =>
        {
            await release.Task; // Deliberately ignore cancellation until the actual write guard.
            var result = fixture.Clipboard.Publish(text, current, token);
            finished.TrySetResult(true);
            return result;
        };
        await fixture.Run(Snapshot(1, "An old translation")).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(fixture.Shown);
        Assert.Contains("自动复制超时", Assert.Single(fixture.CopyFeedback));
        fixture.Clipboard.Copy = null;
        await fixture.Run(Snapshot(2, "A newer translation"));
        release.TrySetResult(true);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("译文：A newer translation", Assert.Single(fixture.Clipboard.Copies));
        Assert.Contains("已复制译文", fixture.Services.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlySuccessfulCopiesIncrementStatisticsIncludingCachedTranslations(bool fails)
    {
        using var fixture = new Fixture();
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PinyinTranslate-selection-autocopy-tests"));
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        try
        {
            var statistics = new StatisticsStore(Path.Combine(directory, "statistics.json"));
            fixture.Set("_statistics", statistics);
            if (fails) fixture.Clipboard.Copy = (_, _, _) => Task.FromResult(new TranslationClipboardCopyResult(false, false, "ClipboardBusy"));
            await fixture.Run(Snapshot(1, "The cached source"));
            await fixture.Run(Snapshot(2, "The cached source"));
            var data = statistics.Snapshot().Data;
            Assert.Single(fixture.Translated);
            Assert.Equal(2, data.SelectionResults);
            Assert.Equal(1, data.CachedResults);
            Assert.Equal(fails ? 0 : 2, data.TranslationCopies);
            Assert.Equal(fails ? 0 : 2 * StatisticsStore.Characters("译文：The cached source"), data.CopiedCharacters);
            Assert.Equal(0, data.OriginalCopies);
            Assert.Equal(0, data.SelectionFailures);
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            if (Path.GetDirectoryName(resolved) == root && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    [Theory]
    [InlineData(false, 499)]
    [InlineData(true, 499)]
    [InlineData(false, 500)]
    [InlineData(true, 500)]
    [InlineData(false, 501)]
    [InlineData(true, 501)]
    [InlineData(false, 4000)]
    [InlineData(true, 4000)]
    public async Task BothSelectionSourcesShareThe500CharacterLimitAndPreserveTheClipboardWhenOverLimit(bool copied, int length)
    {
        using var fixture = new Fixture();
        string text = new('中', length);
        string originalClipboard = copied ? text : "An ordinary clipboard value";
        fixture.Clipboard.Text = originalClipboard;
        await fixture.Run(Snapshot(1, text) with { IsCopiedText = copied });
        if (length <= 500)
        {
            Assert.Equal(text, Assert.Single(fixture.Translated));
            Assert.Equal("译文：" + text, Assert.Single(fixture.Clipboard.Copies));
            Assert.Single(fixture.Shown);
        }
        else
        {
            Assert.Empty(fixture.Translated);
            Assert.Empty(fixture.Shown);
            Assert.Equal(0, fixture.Monitor.Validations);
            Assert.Equal(0, fixture.Clipboard.Attempts);
            Assert.Equal(originalClipboard, fixture.Clipboard.Text);
            Assert.Contains("超过自动翻译上限（500 字符）", fixture.Services.Status);
            Assert.Contains("正常复制不受影响", fixture.Services.Status);
            Assert.DoesNotContain("正在翻译", fixture.Services.Status);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpacesNewlinesPunctuationAndUtf16SurrogatesCountTowardTheSameLimit(bool copied)
    {
        using var fixture = new Fixture();
        string text = new string('a', 497) + " \n。";
        await fixture.Run(Snapshot(1, text) with { IsCopiedText = copied });
        Assert.Equal(500, text.Length);
        Assert.Equal(text, Assert.Single(fixture.Translated));
        await fixture.Run(Snapshot(2, text + "!") with { IsCopiedText = copied });
        Assert.Single(fixture.Translated);
        fixture.ChangeLimit(3);
        await fixture.Run(Snapshot(10, "😀!") with { IsCopiedText = copied });
        Assert.Equal("😀!", fixture.Translated.Last());
        await fixture.Run(Snapshot(11, "😀!!") with { IsCopiedText = copied });
        Assert.Equal(2, fixture.Translated.Count);
        Assert.Contains("超过自动翻译上限（3 字符）", fixture.Services.Status);
    }

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(true, 1, 2)]
    [InlineData(false, 4000, 4000)]
    [InlineData(true, 4000, 4001)]
    public async Task CustomLimitsAreAppliedToTheEntireSourceAtTheirMinimumAndMaximum(bool copied, int limit, int length)
    {
        using var fixture = new Fixture();
        fixture.ChangeLimit(limit);
        string text = new('字', length);
        await fixture.Run(Snapshot(10, text) with { IsCopiedText = copied });
        if (length <= limit) Assert.Equal(text, Assert.Single(fixture.Translated));
        else
        {
            Assert.Empty(fixture.Translated);
            Assert.Empty(fixture.Clipboard.Copies);
            Assert.Contains($"超过自动翻译上限（{limit} 字符）", fixture.Services.Status);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RaisingTheLimitAllowsACompleteTranslationAndLoweringItBlocksEvenACachedResult(bool copied)
    {
        using var fixture = new Fixture();
        string text = new('文', 600);
        await fixture.Run(Snapshot(1, text) with { IsCopiedText = copied });
        Assert.Empty(fixture.Translated);
        fixture.ChangeLimit(1000);
        await fixture.Run(Snapshot(10, text) with { IsCopiedText = copied });
        Assert.Equal(text, Assert.Single(fixture.Translated)); // The complete source; no truncation or splitting.
        Assert.Single(fixture.Clipboard.Copies);
        fixture.ChangeLimit(500);
        fixture.Clipboard.Text = "Ordinary copied content";
        await fixture.Run(Snapshot(20, text) with { IsCopiedText = copied });
        Assert.Single(fixture.Translated);
        Assert.Single(fixture.Shown);
        Assert.Single(fixture.Clipboard.Copies);
        Assert.Equal("Ordinary copied content", fixture.Clipboard.Text);
        Assert.Contains("超过自动翻译上限", fixture.Services.Status);
    }

    [Fact]
    public async Task SavingALowerLimitDuringValidationCancelsBeforeTheApiCanStart()
    {
        using var fixture = new Fixture();
        var entered = Completion<bool>();
        var release = Completion<bool>();
        fixture.Monitor.AsyncValidation = (_, _) => { entered.TrySetResult(true); return release.Task; };
        Task request = fixture.Run(Snapshot(1, new string('文', 400)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.ChangeLimit(200);
        await request.WaitAsync(TimeSpan.FromSeconds(3));
        release.TrySetResult(true);
        Assert.Empty(fixture.Translated);
        Assert.Empty(fixture.Clipboard.Copies);
        Assert.Contains("200 字符", fixture.Services.Status);
    }

    [Fact]
    public async Task SavingALowerLimitDiscardsAnInFlightResponseAndDoesNotOverwriteANewClipboard()
    {
        using var fixture = new Fixture();
        var entered = Completion<bool>();
        var release = Completion<string>();
        fixture.Translate = (_, _) => { entered.TrySetResult(true); return release.Task; };
        Task old = fixture.Run(Snapshot(1, new string('文', 400)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.ChangeLimit(200);
        await old.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Clipboard.Text = "The user's newer copy";
        release.TrySetResult("A late translation");
        Assert.Empty(fixture.Shown);
        Assert.Empty(fixture.Clipboard.Copies);
        Assert.Equal("The user's newer copy", fixture.Clipboard.Text);
        fixture.Translate = (_, _) => Task.FromResult("A new short translation");
        await fixture.Run(Snapshot(10, "A new short source"));
        Assert.Equal("A new short translation", Assert.Single(fixture.Clipboard.Copies));
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
        public FakeClipboardWriter Clipboard { get; } = new();
        public ConcurrentQueue<string> CopyFeedback { get; } = new();
        public Func<string, CancellationToken, Task<string>> Translate { get; set; } = (text, _) => Task.FromResult("译文：" + text);
        public string Target { get; set; } = TranslationLanguages.SimplifiedChinese;
        public Action<CancellationToken>? RequestCreated { get; set; }
        public Func<SelectionSnapshot, string, CancellationToken, Func<bool>, Task> Present { get; set; }

        public Fixture()
        {
            Present = (source, text, _, current) => { if (current()) Shown.Enqueue((source, text)); return Task.CompletedTask; };
            Set("_gate", new object());
            Set("_selection", Monitor);
            Set("_selectionClipboardWriter", Clipboard);
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
            Set("_presentSelection", new Func<SelectionSnapshot, TextTranslation, string, CancellationToken, Func<bool>, Task>(
                (source, text, feedback, token, current) =>
                {
                    if (current()) { ShownTargets.Enqueue(text.TargetLanguage); CopyFeedback.Enqueue(feedback); }
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
        public void SetAutoCopy(bool enabled) => typeof(DesktopServices).GetMethod("CommitSettings", PrivateInstance)!.Invoke(Services,
            [Services.Settings with { SelectionAutoCopy = enabled }]);
        public void ChangeLimit(int limit) => typeof(DesktopServices).GetMethod("CommitSettings", PrivateInstance)!.Invoke(Services,
            [Services.Settings with { SelectionTranslationCharacterLimit = limit }]);
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

    private sealed class FakeClipboardWriter : ITranslationClipboardWriter
    {
        public string? Text { get; set; }
        public ConcurrentQueue<string> Copies { get; } = new();
        public int Attempts;
        public Func<string, Func<bool>, CancellationToken, Task<TranslationClipboardCopyResult>>? Copy;
        public Task<TranslationClipboardCopyResult> CopyAsync(string text, Func<bool> current, CancellationToken token)
        {
            Interlocked.Increment(ref Attempts);
            if (Copy is not null) return Copy(text, current, token);
            return Task.FromResult(Publish(text, current, token));
        }
        public TranslationClipboardCopyResult Publish(string text, Func<bool> current, CancellationToken token)
        {
            if (token.IsCancellationRequested || !current()) return new(false, false, "ScopeExpired");
            Copies.Enqueue(text);
            Text = text;
            return new(true, true, "Copied");
        }
        public void Dispose() { }
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
