using System.Collections.ObjectModel;
using PinyinTranslate.Core;

namespace PinyinTranslate.App;

public sealed record AppSettings(string ImeHotkey, string SelectionHotkey, string ScreenshotHotkey, string Theme = "light",
    string ImeTargetLanguage = TranslationLanguages.English,
    string SelectionLanguageA = TranslationLanguages.SimplifiedChinese,
    string SelectionLanguageB = TranslationLanguages.English,
    string ScreenshotTargetLanguage = TranslationLanguages.SimplifiedChinese,
    bool SelectionAutoCopy = true)
{
    public void ValidateLanguages()
    {
        _ = TranslationLanguages.Get(ImeTargetLanguage);
        _ = TranslationLanguages.Get(ScreenshotTargetLanguage);
        TranslationLanguages.ValidatePair(SelectionLanguageA, SelectionLanguageB);
    }

    // Old settings have no language fields. Invalid/newer identifiers never reach prompts or caches.
    public AppSettings NormalizeLanguages()
    {
        string a = TranslationLanguages.IsSupported(SelectionLanguageA) ? SelectionLanguageA : TranslationLanguages.SimplifiedChinese;
        string b = TranslationLanguages.IsSupported(SelectionLanguageB) ? SelectionLanguageB : TranslationLanguages.English;
        if (a == b) { a = TranslationLanguages.SimplifiedChinese; b = TranslationLanguages.English; }
        return this with
        {
            ImeTargetLanguage = TranslationLanguages.IsSupported(ImeTargetLanguage) ? ImeTargetLanguage : TranslationLanguages.English,
            SelectionLanguageA = a, SelectionLanguageB = b,
            ScreenshotTargetLanguage = TranslationLanguages.IsSupported(ScreenshotTargetLanguage) ? ScreenshotTargetLanguage : TranslationLanguages.SimplifiedChinese
        };
    }
}
public sealed record ScreenshotItem(string Id, string Original, string Translation, bool Uncertain = false);
public sealed record ScreenshotResult(IReadOnlyList<ScreenshotItem> Items, bool IsComplete, string? Warning,
    string TargetLanguage = TranslationLanguages.SimplifiedChinese);

/// <summary>UI boundary. Implementations must invalidate outstanding operations when a mode is disabled.</summary>
public interface IAppServices : IDisposable
{
    bool ImeEnabled { get; }
    bool SelectionEnabled { get; }
    bool IsPreparingForUpdate => false;
    string Status { get; }
    AppSettings Settings { get; }
    event EventHandler? StateChanged;
    event EventHandler? ScreenshotRequested;
    event EventHandler? StatisticsChanged;
    Task<string?> SetImeEnabledAsync(bool enabled);
    Task<string?> SetSelectionEnabledAsync(bool enabled);
    Task PrepareForUpdateAsync(CancellationToken cancellationToken);
    Task ResumeAfterUpdateFailureAsync();
    Task<string?> ApplySettingsAsync(AppSettings settings);
    Task<string?> SetThemeAsync(string theme);
    string? GetSavedApiKey();
    Task<string?> SaveApiKeyAsync(string key);
    IDisposable SuspendSelectionForCapture();
    Task<ScreenshotResult> TranslateScreenshotAsync(byte[] png, CancellationToken cancellationToken);
    StatisticsSnapshot GetStatistics();
    void RecordCopy(string text, bool original);
}

/// <summary>A deliberately unavailable service until platform and translation components are connected.</summary>
public sealed class UnavailableAppServices : IAppServices
{
    public bool ImeEnabled => false;
    public bool SelectionEnabled => false;
    public bool IsPreparingForUpdate => false;
    public string Status => "平台服务尚未连接；输入、划词和截图翻译不可用。";
    public AppSettings Settings => new("Ctrl+Alt+T", "Ctrl+Alt+D", "Ctrl+Alt+S");
    public event EventHandler? StateChanged { add { } remove { } }
    public event EventHandler? ScreenshotRequested { add { } remove { } }
    public event EventHandler? StatisticsChanged { add { } remove { } }
    public Task<string?> SetImeEnabledAsync(bool enabled) => Task.FromResult<string?>(enabled ? "微软拼音输入服务尚未连接。" : null);
    public Task<string?> SetSelectionEnabledAsync(bool enabled) => Task.FromResult<string?>(enabled ? "划词读取服务尚未连接。" : null);
    public Task PrepareForUpdateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
    public Task ResumeAfterUpdateFailureAsync() => Task.CompletedTask;
    public Task<string?> ApplySettingsAsync(AppSettings settings) => Task.FromResult<string?>("快捷键服务尚未连接。设置未保存。");
    public Task<string?> SetThemeAsync(string theme) => Task.FromResult<string?>("外观设置尚未连接。设置未保存。");
    public string? GetSavedApiKey() => null;
    public Task<string?> SaveApiKeyAsync(string key) => Task.FromResult<string?>("密钥存储服务尚未连接。密钥未保存。");
    public IDisposable SuspendSelectionForCapture() => new EmptyScope();
    private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    public Task<ScreenshotResult> TranslateScreenshotAsync(byte[] png, CancellationToken cancellationToken) => throw new InvalidOperationException("截图翻译服务尚未连接。");
    public StatisticsSnapshot GetStatistics() => new(new TranslationStatistics { SinceUtc = DateTimeOffset.UtcNow }, null, "统计服务尚未连接。");
    public void RecordCopy(string text, bool original) { }
    public void Dispose() { }
}
