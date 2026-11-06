using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PinyinTranslate.Core;
using PinyinTranslate.Selection;
using PinyinTranslate.Input;

namespace PinyinTranslate.App;

/// <summary>Owns local settings, global shortcuts, UIA selection and the one budgeted translation client.</summary>
public sealed class DesktopServices : IAppServices
{
    private readonly Dispatcher _dispatcher;
    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly string _keyPath;
    private readonly HttpClient _http = new();
    private readonly DeepSeekClient _client;
    private readonly BudgetLedger _legacyBudget;
    private readonly ApiUsageLedger _apiUsage;
    private readonly StatisticsStore _statistics;
    private readonly TranslationCache _cache = new();
    private readonly ISelectionMonitor _selection;
    private readonly ITranslationClipboardWriter _selectionClipboardWriter;
    private readonly Func<string, string, string, CancellationToken, Task<TextTranslation>> _selectionTranslator;
    private readonly TimeSpan _selectionTranslationTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _selectionValidationTimeout = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _selectionCopyTimeout = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _selectionStatusOwner;
    private readonly Func<SelectionSnapshot, TextTranslation, string, CancellationToken, Func<bool>, Task> _presentSelection;
    private readonly Action<long> _dismissSelection;
    private readonly HotkeyManager _hotkeys;
    private readonly ImeRuntimeController _ime;
    private ImePreviewWindow? _imePreview;
    private readonly object _gate = new();
    private CancellationTokenSource? _selectionRequest;
    private long _selectionRequestVersion;
    private long _selectionAutoCopyVersion;
    private SelectionResultWindow? _resultWindow;
    private long _resultVersion;
    private string _status = "拼音翻译与划词模式均已关闭。";
    private bool _statusUsesImeCopyHint;
    private string? _shortcutError;
    private int _disposed;
    private long _lastInvalidated;
    private long _selectionToggleVersion;
    private int _captureSuspendCount;
    private readonly UpdatePreparation? _updatePreparation;
    public bool ImeEnabled => _ime.Enabled;
    public bool SelectionEnabled { get; private set; }
    public bool IsPreparingForUpdate => _updatePreparation?.IsActive == true;
    public string Status
    {
        get
        {
            string status;
            string? shortcutError;
            bool useImeCopyHint;
            lock (_gate) { status = _status; shortcutError = _shortcutError; useImeCopyHint = _statusUsesImeCopyHint; }
            // Never acquire the runtime gate while holding the service gate:
            // runtime notices can arrive while it holds its own state lock.
            if (useImeCopyHint) status = _ime.CurrentAcceptanceHint;
            return status + (shortcutError is null ? "" : " " + shortcutError);
        }
    }
    public AppSettings Settings { get; private set; }
    public event EventHandler? StateChanged;
    public event EventHandler? ScreenshotRequested;
    public event EventHandler? StatisticsChanged;

    public DesktopServices(Func<string>? keyProvider = null, string? settingsDirectory = null, Action<string>? selectionDiagnostic = null,
        BudgetLedger? developmentBudget = null, Func<string, CancellationToken, Task<string>>? candidateTranslator = null,
        int? imeTestProcessId = null,
        Func<System.Diagnostics.Process, string, string, ICandidateObserver>? candidateObserverFactory = null,
        ISelectionMonitor? selectionMonitor = null,
        Func<string, string, string, CancellationToken, Task<TextTranslation>>? selectionTranslator = null,
        ITranslationClipboardWriter? selectionClipboardWriter = null)
    {
        _dispatcher = Application.Current.Dispatcher;
        _selection = selectionMonitor ?? new SelectionMonitor(selectionDiagnostic, OwnClipboardWrites.Contains);
        _selectionClipboardWriter = selectionClipboardWriter ?? new WindowsTranslationClipboardWriter();
        _presentSelection = PresentSelectionAsync;
        _dismissSelection = QueueSelectionDismissal;
        var persistentDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinyinTranslate");
        _directory = settingsDirectory ?? persistentDirectory;
        _settingsPath = Path.Combine(_directory, "settings.json");
        _keyPath = Path.Combine(_directory, "secrets", "deepseek.dpapi");
        Settings = LoadSettings();
        // Dependency injection is used by the local desktop test harness to read
        // the protected test credential in-process; the shipping UI uses DPAPI.
        _legacyBudget = new BudgetLedger(Path.Combine(persistentDirectory, "api-budget.json"));
        _apiUsage = new ApiUsageLedger(Path.Combine(_directory, "api-usage.json"));
        _statistics = new StatisticsStore(Path.Combine(_directory, "usage-statistics.json"));
        _statistics.Changed += StatisticsStoreChanged;
        IApiRequestLedger ledger = developmentBudget is null ? _apiUsage : developmentBudget;
        _client = new DeepSeekClient(_http, ledger, keyProvider ?? (() => File.Exists(_keyPath) ? SecretStore.Load(_keyPath) : ""));
        _selectionTranslator = selectionTranslator ?? ((text, a, b, token) => _client.TranslateBidirectionalAsync(text, a, b, "划词翻译", token));
        _ime = new ImeRuntimeController(candidateTranslator ?? TranslateCandidateAsync, imeTestProcessId,
            [TextInputProfile.WinFormsEdit, TextInputProfile.WinFormsRichEdit], candidateObserverFactory);
        _ime.StatusChanged += SetStatus;
        _ime.PreviewChanged += ImePreviewChanged;
        _ime.TranslationCopied += ImeTranslationCopied;
        _ime.CopyPresentationChanged += ImeCopyPresentationChanged;
        _hotkeys = new HotkeyManager(HotkeyFired);
        _shortcutError = _hotkeys.Apply(Settings);
        _selection.Invalidated += SelectionInvalidated;
        _selection.SelectionChanged += SelectionChanged;
        _selection.StatusChanged += SelectionStatus;
        _updatePreparation = new(() => new(_ime.Enabled, SelectionEnabled), PauseForUpdateAsync, RestoreAfterUpdateFailureAsync);
    }
    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath));
                if (settings != null && !string.IsNullOrWhiteSpace(settings.ImeHotkey) && !string.IsNullOrWhiteSpace(settings.SelectionHotkey) && !string.IsNullOrWhiteSpace(settings.ScreenshotHotkey))
                    return (ThemeManager.IsSupported(settings.Theme) ? settings : settings with { Theme = "light" }).NormalizeLanguages();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new("Ctrl+Alt+T", "Ctrl+Alt+D", "Ctrl+Alt+S");
    }
    private void HotkeyFired(int id)
    {
        if (Volatile.Read(ref _disposed) != 0 || IsPreparingForUpdate) return;
        if (id == 1) _ = ToggleImeFromHotkeyAsync();
        if (id == 2) _ = ToggleSelectionFromHotkeyAsync();
        if (id == 3) ScreenshotRequested?.Invoke(this, EventArgs.Empty);
    }
    private async Task ToggleSelectionFromHotkeyAsync()
    {
        var error = await SetSelectionEnabledAsync(!SelectionEnabled);
        if (error != null && Volatile.Read(ref _disposed) == 0) SetStatus(error);
    }
    private async Task ToggleImeFromHotkeyAsync()
    {
        try { var error = await SetImeEnabledAsync(!ImeEnabled); if (error != null) SetStatus(error); }
        catch (Exception error) { SetStatus("输入模式切换失败：" + error.Message); }
    }
    public async Task<string?> SetImeEnabledAsync(bool enabled)
    {
        if (IsPreparingForUpdate) return "正在准备更新，请稍后再切换翻译模式。";
        var error = await _ime.SetEnabledAsync(enabled);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return error;
    }
    private Task<string> TranslateCandidateAsync(string text, CancellationToken token)
    {
        string target = Settings.ImeTargetLanguage;
        return _cache.GetOrTranslateAsync(DeepSeekClient.Model, "zh-" + target, "candidate-v2", text,
            async cancellation =>
            {
                // Keep the existing 100 ms candidate quiet period + 200 ms
                // cache-miss debounce. Cached translations remain instant.
                await Task.Delay(200, cancellation).ConfigureAwait(false);
                return await _client.TranslateAsync(text, target, "微软拼音候选", cancellation).ConfigureAwait(false);
            }, token);
    }
    private void ImePreviewChanged(CandidateTranslationState state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _dispatcher.BeginInvoke(() =>
        {
            var current = _ime.State;
            if (Volatile.Read(ref _disposed) != 0 || state.Generation != current.Generation) return;
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (current.Observation is null) { _imePreview?.Hide(); return; }
            _imePreview ??= new ImePreviewWindow();
            // Translation and copy progress can share a generation. A queued
            // waiting notification must not repaint an older stage over success.
            _imePreview.Present(current, _ime.CurrentAcceptanceHint);
        });
    }
    private void ImeTranslationCopied(string text) => RecordCopy(text, false);
    private void ImeCopyPresentationChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_gate) { _statusUsesImeCopyHint = true; _selectionStatusOwner = null; }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task<string?> SetSelectionEnabledAsync(bool enabled)
        => await SetSelectionEnabledCoreAsync(enabled, restoring: false);

    private async Task<string?> SetSelectionEnabledCoreAsync(bool enabled, bool restoring)
    {
        if (!restoring && IsPreparingForUpdate) return "正在准备更新，请稍后再切换翻译模式。";
        long toggleVersion = Interlocked.Increment(ref _selectionToggleVersion);
        if (Volatile.Read(ref _disposed) != 0) return "程序正在退出。";
        if (enabled)
        {
            try { if (!await _selection.Ready.WaitAsync(TimeSpan.FromSeconds(3))) return "选区监听无法启动。"; }
            catch (TimeoutException) { return "选区监听启动超时，未开启。"; }
            if (Volatile.Read(ref _disposed) != 0) return "程序正在退出。";
            if (!restoring && IsPreparingForUpdate) return "正在准备更新，请稍后再开启翻译。";
            if (Interlocked.Read(ref _selectionToggleVersion) != toggleVersion) return null;
        }
        SelectionEnabled = enabled; _selection.SetEnabled(enabled && _captureSuspendCount == 0);
        SetStatus(enabled ? $"划词双向翻译已开启：{TranslationLanguages.Get(Settings.SelectionLanguageA).DisplayName} ↔ {TranslationLanguages.Get(Settings.SelectionLanguageB).DisplayName}。读不到选区时，选中后按 Ctrl+C 兼容翻译。" : "划词自动翻译与复制兼容翻译均已关闭。");
        if (!enabled) CloseSelectionWindow();
        return null;
    }

    /// <summary>Called on the UI dispatcher; preparation is reversible until normal application shutdown.</summary>
    public Task PrepareForUpdateAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("程序正在退出，无法准备更新。");
        return _updatePreparation?.PrepareAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public Task ResumeAfterUpdateFailureAsync() => _updatePreparation?.ResumeAsync() ?? Task.CompletedTask;

    private async Task PauseForUpdateAsync(CancellationToken cancellationToken)
    {
        // Blocked by IsPreparingForUpdate before this method is entered.
        // Revoke in-flight enable and selection operations before awaiting bridges.
        Interlocked.Increment(ref _selectionToggleVersion);
        SelectionEnabled = false;
        _selection.SetEnabled(false);
        lock (_gate) CancelSelectionRequest(_selectionRequest);
        CloseSelectionWindow();
        _imePreview?.Hide();
        StateChanged?.Invoke(this, EventArgs.Empty);
        await _ime.StopForUpdateAsync(cancellationToken);
    }

    private async Task RestoreAfterUpdateFailureAsync(UpdateModeState modes)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ime.ResumeAfterUpdateFailure();
        string? imeError = await _ime.SetEnabledAsync(modes.ImeEnabled);
        string? selectionError = await SetSelectionEnabledCoreAsync(modes.SelectionEnabled, restoring: true);
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (imeError is not null || selectionError is not null)
            throw new InvalidOperationException("部分翻译模式未能恢复，请在主界面重新开启。");
    }
    public Task<string?> ApplySettingsAsync(AppSettings settings)
    {
        if (IsPreparingForUpdate) return Task.FromResult<string?>("正在准备更新，请稍后再保存设置。");
        try { settings.ValidateLanguages(); }
        catch (ArgumentException languageError) { return Task.FromResult<string?>(languageError.Message); }
        var old = Settings;
        bool hotkeysChanged = old.ImeHotkey != settings.ImeHotkey || old.SelectionHotkey != settings.SelectionHotkey || old.ScreenshotHotkey != settings.ScreenshotHotkey || _shortcutError is not null;
        var error = hotkeysChanged ? _hotkeys.Apply(settings) : null;
        if (error != null) return Task.FromResult<string?>(error);
        try
        {
            WriteSettings(settings);
            CommitSettings(settings);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult<string?>(null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var rollback = hotkeysChanged ? _hotkeys.Apply(old) : null;
            _shortcutError = rollback;
            return Task.FromResult<string?>("保存设置失败：" + e.Message + (rollback is null ? "" : "；原快捷键恢复失败。"));
        }
    }

    private void CommitSettings(AppSettings settings)
    {
        var old = Settings;
        bool inputChanged = old.ImeTargetLanguage != settings.ImeTargetLanguage;
        bool pairChanged = old.SelectionLanguageA != settings.SelectionLanguageA || old.SelectionLanguageB != settings.SelectionLanguageB;
        void Commit()
        {
            lock (_gate)
            {
                if (old.SelectionAutoCopy != settings.SelectionAutoCopy) Interlocked.Increment(ref _selectionAutoCopyVersion);
                Settings = settings; _shortcutError = null;
                if (pairChanged)
                {
                    CancelSelectionRequest(_selectionRequest);
                    _selectionRequest = null; _selectionStatusOwner = null;
                }
            }
        }
        // Lock order stays runtime -> service. Copy guards observe the new
        // setting and revocation of the old generation atomically.
        if (inputChanged) _ime.ChangeTranslationTarget(Commit);
        else Commit();
        if (pairChanged)
        {
            CloseSelectionWindow();
            _selection.SetEnabled(SelectionEnabled && _captureSuspendCount == 0, requireFreshSelection: true);
        }
        if (inputChanged || pairChanged || old.ScreenshotTargetLanguage != settings.ScreenshotTargetLanguage)
            SetStatus("翻译语言已保存；划词请重新选择文字，拼音请继续输入。已打开的截图保留原目标语言。");
    }
    public Task<string?> SetThemeAsync(string theme)
    {
        if (IsPreparingForUpdate) return Task.FromResult<string?>("正在准备更新，请稍后再切换外观。");
        if (!ThemeManager.IsSupported(theme)) return Task.FromResult<string?>("无法识别所选风格。");
        if (Settings.Theme == theme) return Task.FromResult<string?>(null);
        try
        {
            var updated = Settings with { Theme = theme };
            WriteSettings(updated);
            Settings = updated;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult<string?>(null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Task.FromResult<string?>("风格保存失败，请稍后重试。"); }
    }
    private void WriteSettings(AppSettings settings)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _settingsPath, true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
    public string? GetSavedApiKey()
    {
        if (!File.Exists(_keyPath)) return null;
        try { return SecretStore.Load(_keyPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { throw new InvalidOperationException("无法读取已保存的密钥，请重新输入并保存。", e); }
    }
    public Task<string?> SaveApiKeyAsync(string key)
    {
        if (IsPreparingForUpdate) return Task.FromResult<string?>("正在准备更新，请稍后再保存密钥。");
        try { SecretStore.Save(_keyPath, key); return Task.FromResult<string?>(null); }
        catch (Exception e) { return Task.FromResult<string?>("密钥保存失败：" + e.Message); }
    }
    public IDisposable SuspendSelectionForCapture()
    {
        if (IsPreparingForUpdate) throw new InvalidOperationException("正在准备更新，请稍后再截图。");
        if (!_dispatcher.CheckAccess()) throw new InvalidOperationException("截图必须在界面线程启动。");
        _captureSuspendCount++;
        if (_captureSuspendCount == 1) { _selection.SetEnabled(false); CloseSelectionWindow(); }
        return new CaptureScope(this);
    }
    private sealed class CaptureScope(DesktopServices owner) : IDisposable
    {
        private DesktopServices? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value == null) return;
            if (--value._captureSuspendCount == 0 && value.SelectionEnabled && Volatile.Read(ref value._disposed) == 0 && !value.IsPreparingForUpdate)
                value._selection.SetEnabled(true, requireFreshSelection: true);
        }
    }
    public async Task<ScreenshotResult> TranslateScreenshotAsync(byte[] png, CancellationToken cancellationToken)
    {
        if (IsPreparingForUpdate) throw new InvalidOperationException("正在准备更新，请稍后再截图翻译。");
        string target = Settings.ScreenshotTargetLanguage;
        try
        {
            var image = await _client.TranslateImageAsync(png, target, "截图翻译", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            RecordStatistic(() => _statistics.Result(true,
                image.Groups.Sum(x => StatisticsStore.Characters(x.Source)),
                image.Groups.Sum(x => StatisticsStore.Characters(x.Translation))));
            return new(image.Groups.Select(x => new ScreenshotItem(x.Id, x.Source, x.Translation, x.Uncertain)).ToList(), !image.NeedsReview, image.Status, target);
        }
        catch (OperationCanceledException) { RecordStatistic(() => _statistics.Failure(true, cancellationToken.IsCancellationRequested)); throw; }
        catch (Exception) { RecordStatistic(() => _statistics.Failure(true, false)); throw; }
    }

    public StatisticsSnapshot GetStatistics()
    {
        var (data, warning) = _statistics.Snapshot();
        var current = _apiUsage.Snapshot();
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(warning)) warnings.Add(warning);
        if (!string.IsNullOrWhiteSpace(current.Warning)) warnings.Add(current.Warning);
        BudgetSnapshot? legacy = null;
        try { legacy = _legacyBudget.Snapshot(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException)
        { warnings.Add("旧版 API 用量记录无法读取，统计只包含新版记录。"); }

        var api = ApiStatisticsCalculator.Create(current, legacy, DateTimeOffset.UtcNow);
        return new(data, api, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    public void RecordCopy(string text, bool original)
    {
        if (string.IsNullOrEmpty(text)) return;
        RecordStatistic(() => _statistics.Copy(original, StatisticsStore.Characters(text)));
    }

    private static void RecordStatistic(Action record)
    {
        // Statistics are supplementary and must never interrupt translation or copying.
        try { record(); } catch (Exception) { }
    }

    private void StatisticsStoreChanged(object? sender, EventArgs e)
    {
        try { StatisticsChanged?.Invoke(this, EventArgs.Empty); } catch (Exception) { }
    }
    private void SelectionStatus(string status)
    {
        lock (_gate)
        {
            if (!SelectionEnabled || Volatile.Read(ref _disposed) != 0 || IsPreparingForUpdate ||
                _selectionRequest is { IsCancellationRequested: false }) return;
            _status = status; _statusUsesImeCopyHint = false; _selectionStatusOwner = null;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void SelectionInvalidated(long version)
    {
        bool changed = false;
        lock (_gate)
        {
            if (version <= _lastInvalidated) return;
            Interlocked.Exchange(ref _lastInvalidated, version);
            if (_selectionRequest != null && _selectionRequestVersion < version)
            {
                CancelSelectionRequest(_selectionRequest);
                if (ReferenceEquals(_selectionStatusOwner, _selectionRequest))
                {
                    _selectionStatusOwner = null;
                    if (Volatile.Read(ref _disposed) == 0 && !IsPreparingForUpdate)
                    {
                        _status = "选区已变化，本轮翻译已取消；等待新的选中文字。";
                        _statusUsesImeCopyHint = false;
                        changed = true;
                    }
                }
            }
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
        _dismissSelection(version);
    }
    private void SelectionChanged(SelectionSnapshot snapshot)
    {
        if (!_selection.IsCurrent(snapshot.Version) || Volatile.Read(ref _disposed) != 0 || IsPreparingForUpdate) return;
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (!_selection.IsCurrent(snapshot.Version) || IsPreparingForUpdate) { cts.Dispose(); return; }
            if (_selectionRequestVersion == snapshot.Version && _selectionRequest is { IsCancellationRequested: false })
            { cts.Dispose(); return; }
            CancelSelectionRequest(_selectionRequest); _selectionRequest = cts; _selectionRequestVersion = snapshot.Version;
        }
        _ = TranslateSelectionAsync(snapshot, cts);
    }
    private async Task TranslateSelectionAsync(SelectionSnapshot snapshot, CancellationTokenSource cts)
    {
        bool started = false, recorded = false;
        AppSettings settings;
        long autoCopyVersion;
        lock (_gate) { settings = Settings; autoCopyVersion = _selectionAutoCopyVersion; }
        string languageA = settings.SelectionLanguageA, languageB = settings.SelectionLanguageB;
        CancellationTokenSource? deadline = null;
        var translationTimeout = _selectionTranslationTimeout > TimeSpan.Zero ? _selectionTranslationTimeout : TimeSpan.FromSeconds(20);
        var validationTimeout = _selectionValidationTimeout > TimeSpan.Zero ? _selectionValidationTimeout : TimeSpan.FromSeconds(3);
        try
        {
            if (cts.IsCancellationRequested || !_selection.IsCurrent(snapshot.Version)) return;
            SetSelectionStatus(snapshot, cts, snapshot.IsCopiedText ? "正在确认本次复制的文字…" : "正在确认选中文字…", claim: true);
            if (!await _selection.ValidateAsync(snapshot).WaitAsync(validationTimeout, cts.Token).ConfigureAwait(false))
            {
                SetSelectionStatus(snapshot, cts, "选区暂时无法确认，本轮已结束；请重新划选或使用截图翻译。", terminal: true);
                return;
            }
            if (cts.IsCancellationRequested || !_selection.IsCurrent(snapshot.Version)) return;
            string direction = $"auto:{languageA}:{languageB}";
            string label = $"{TranslationLanguages.Get(languageA).DisplayName} ↔ {TranslationLanguages.Get(languageB).DisplayName}";
            started = true;
            SetSelectionStatus(snapshot, cts, $"正在翻译（{label}）…");
            deadline = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            deadline.CancelAfter(translationTimeout);
            var cached = await _cache.GetOrTranslateAsync(DeepSeekClient.Model, direction, "selection-v3", snapshot.Text,
                async token =>
                {
                    var result = await _selectionTranslator(snapshot.Text, languageA, languageB, token).ConfigureAwait(false);
                    // Validate injected providers too; never cache a mislabeled result.
                    return TextTranslation.Parse(result.ToCacheValue(), languageA, languageB).ToCacheValue();
                }, out var origin, deadline.Token).ConfigureAwait(false);
            var translated = TextTranslation.Parse(cached, languageA, languageB);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            if (cts.IsCancellationRequested) return;
            RecordStatistic(() => _statistics.Result(false, StatisticsStore.Characters(snapshot.Text), StatisticsStore.Characters(translated.Translation), origin));
            recorded = true;
            SetSelectionStatus(snapshot, cts, "译文已返回，正在确认选区…");
            if (!await _selection.ValidateAsync(snapshot).WaitAsync(validationTimeout, cts.Token).ConfigureAwait(false))
            {
                SetSelectionStatus(snapshot, cts, "译文已返回，但选区暂时无法确认；本轮已结束，请重新划选。", terminal: true);
                return;
            }
            bool Current() => Volatile.Read(ref _disposed) == 0 && !cts.IsCancellationRequested && SelectionEnabled &&
                !IsPreparingForUpdate && Volatile.Read(ref _captureSuspendCount) == 0 &&
                ReferenceEquals(Volatile.Read(ref _selectionRequest), cts) && _selection.IsCurrent(snapshot.Version) &&
                Settings.SelectionLanguageA == languageA && Settings.SelectionLanguageB == languageB;
            if (!Current()) return;
            string copyFeedback = await CopySelectionTranslationAsync(snapshot, translated.Translation, cts,
                settings.SelectionAutoCopy, autoCopyVersion, Current).ConfigureAwait(false);
            if (!Current()) return;
            await _presentSelection(snapshot, translated, copyFeedback, cts.Token, Current).ConfigureAwait(false);
            if (Current()) SetSelectionStatus(snapshot, cts, $"{(snapshot.IsCopiedText ? "复制兼容" : "划词")}翻译完成（译为{TranslationLanguages.Get(translated.TargetLanguage).DisplayName}）。{copyFeedback}", terminal: true);
        }
        catch (OperationCanceledException)
        {
            if (started && !recorded) { RecordStatistic(() => _statistics.Failure(false, cts.IsCancellationRequested)); recorded = true; }
            string message = !cts.IsCancellationRequested && deadline?.IsCancellationRequested == true
                ? $"划词翻译超时（{translationTimeout.TotalSeconds:0.#} 秒），本轮已结束；请稍后重新划选重试。"
                : cts.IsCancellationRequested ? "本轮划词翻译已取消，等待新的选中文字。"
                : "划词翻译请求超时或已取消，请重新划选重试。";
            SetSelectionStatus(snapshot, cts, message, terminal: true);
        }
        catch (TimeoutException)
        {
            if (started && !recorded) { RecordStatistic(() => _statistics.Failure(false, false)); recorded = true; }
            SetSelectionStatus(snapshot, cts, "选区确认超时，本轮已结束；请重新划选或使用截图翻译。", terminal: true);
        }
        catch (Exception e)
        {
            if (started && !recorded) { RecordStatistic(() => _statistics.Failure(false, cts.IsCancellationRequested)); recorded = true; }
            if (!cts.IsCancellationRequested) SetSelectionStatus(snapshot, cts, "划词翻译失败：" + e.Message, terminal: true);
        }
        finally
        {
            if (started && !recorded) RecordStatistic(() => _statistics.Failure(false, true));
            SetSelectionStatus(snapshot, cts, "本轮划词翻译已结束，等待新的选中文字。", terminal: true);
            lock (_gate) { if (ReferenceEquals(_selectionRequest, cts)) _selectionRequest = null; }
            deadline?.Dispose();
            _ = DisposeSelectionRequestAsync(cts);
        }
    }

    private async Task<string> CopySelectionTranslationAsync(SelectionSnapshot snapshot, string translation,
        CancellationTokenSource request, bool enabledAtStart, long settingsVersion, Func<bool> current)
    {
        const string manualCopy = "点击译文可复制。";
        bool Allowed() => enabledAtStart && Settings.SelectionAutoCopy &&
            Interlocked.Read(ref _selectionAutoCopyVersion) == settingsVersion && current();
        if (!Allowed()) return manualCopy;
        SetSelectionStatus(snapshot, request, "译文已返回，正在复制译文…");
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
        deadline.CancelAfter(_selectionCopyTimeout > TimeSpan.Zero ? _selectionCopyTimeout : TimeSpan.FromSeconds(2));
        bool CopyCurrent() => !deadline.IsCancellationRequested && Allowed();
        try
        {
            var result = await _selectionClipboardWriter.CopyAsync(translation, CopyCurrent, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (result.Copied)
            {
                RecordCopy(translation, false);
                return "已复制译文，可按 Ctrl+V 粘贴。";
            }
            return Allowed() ? "自动复制未完成，点击译文可复制。" : manualCopy;
        }
        catch (OperationCanceledException) when (!request.IsCancellationRequested)
        {
            return Allowed() ? "自动复制超时，点击译文可复制。" : manualCopy;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return "自动复制失败，点击译文可复制。"; }
        finally
        {
            // A timed-out or canceled caller must revoke the native worker too;
            // it rechecks this token and scope before changing the clipboard.
            _ = DisposeSelectionRequestAsync(deadline);
        }
    }

    private void SetSelectionStatus(SelectionSnapshot snapshot, CancellationTokenSource request, string message,
        bool claim = false, bool terminal = false)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || IsPreparingForUpdate ||
                !ReferenceEquals(_selectionRequest, request) || _selectionRequestVersion != snapshot.Version ||
                (!claim && !ReferenceEquals(_selectionStatusOwner, request))) return;
            if (!terminal && (request.IsCancellationRequested || !_selection.IsCurrent(snapshot.Version))) return;
            _status = message; _statusUsesImeCopyHint = false;
            _selectionStatusOwner = terminal ? null : request;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task DisposeSelectionRequestAsync(CancellationTokenSource request)
    {
        try { await ObserveSelectionCancellationAsync(request.CancelAsync()).ConfigureAwait(false); }
        finally { request.Dispose(); }
    }
    private async Task PresentSelectionAsync(SelectionSnapshot snapshot, TextTranslation translated, string copyFeedback, CancellationToken token, Func<bool> current)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (!current()) return;
            CloseSelectionWindow();
            _resultWindow = new SelectionResultWindow(snapshot.Text, translated.Translation, RecordCopy, translated.TargetLanguage, snapshot.IsCopiedText,
                copyFeedback);
            _resultVersion = snapshot.Version;
            _resultWindow.Show();
        }, DispatcherPriority.Normal, token);
    }
    private void QueueSelectionDismissal(long version) => _dispatcher.BeginInvoke(() =>
    {
        if (Interlocked.Read(ref _lastInvalidated) == version && _resultVersion < version) CloseSelectionWindow();
    });
    private static void CancelSelectionRequest(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { _ = ObserveSelectionCancellationAsync(source.CancelAsync()); }
        catch (ObjectDisposedException) { }
    }
    private static async Task ObserveSelectionCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); } catch (Exception) { }
    }
    private void CloseSelectionWindow() { _resultWindow?.Close(); _resultWindow = null; }
    private void SetStatus(string status)
    {
        lock (_gate) { _status = status; _statusUsesImeCopyHint = false; _selectionStatusOwner = null; }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate) { CancelSelectionRequest(_selectionRequest); }
        _selection.Invalidated -= SelectionInvalidated; _selection.SelectionChanged -= SelectionChanged; _selection.StatusChanged -= SelectionStatus;
        _statistics.Changed -= StatisticsStoreChanged;
        _ime.PreviewChanged -= ImePreviewChanged; _ime.StatusChanged -= SetStatus;
        _ime.TranslationCopied -= ImeTranslationCopied;
        _ime.CopyPresentationChanged -= ImeCopyPresentationChanged;
        _ime.Dispose(); _imePreview?.Close();
        _selection.Dispose(); _selectionClipboardWriter.Dispose(); CloseSelectionWindow(); _hotkeys.Dispose(); _cache.Dispose(); _http.Dispose();
    }
}
