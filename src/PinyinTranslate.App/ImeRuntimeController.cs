using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PinyinTranslate.Input;

namespace PinyinTranslate.App;

/// <summary>Connects real candidates, translation and capability-scoped plain-text acceptance.</summary>
internal sealed class ImeRuntimeController : IDisposable
{
    private const string UnsupportedContextStatus = "此控件暂不支持拼音翻译和 Tab 输入；可使用划词或截图翻译。";
    private const string PreviewOnlyStatus = "仅预览，暂不可自动输入；普通 Tab 保持原作用。";
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private readonly object _gate = new();
    private readonly EditableContextAdapter _contexts;
    private readonly Func<CancellationToken, Task<EditableContextSnapshot?>> _readContextAsync;
    private readonly Func<nint> _foregroundWindow;
    private readonly Func<EditableContextSnapshot, BaselineAnchor?, string, CancellationToken, Func<bool>, Func<bool>?, Task<EditableReplaceResult>> _replaceAsync;
    private readonly ImeWriteDiagnostics? _writeDiagnostics;
    private readonly bool _copyOnly;
    private readonly ITranslationClipboardWriter? _clipboardWriter;
    private CopyTicket? _copyTicket;
    private CopyState _copyState;
    private string? _copyFailureReason;
    private readonly CandidateTranslationCoordinator _coordinator;
    private readonly InputKeyboardHook _keyboard;
    private readonly InputPointerHook _pointer;
    private readonly int? _testProcessId;
    private readonly Func<Process, string, string, ICandidateObserver> _observerFactory;
    // Null only in legacy uninitialized headless fixtures.
    private readonly UpdateQuiescenceTracker? _updateQuiescence = new();
    private readonly Func<Task>? _disableForUpdateAsync;
    private readonly object _diagnosticGate = new();
    private CancellationTokenSource? _pollCancellation;
    private Binding? _binding;
    private Binding? _writingBinding, _deferredRelease;
    private EditableContextSnapshot? _context;
    private BaselineAnchor? _anchor;
    private Publication? _publication;
    private Publication? _writingPublication;
    private string? _writeProgressHint;
    private string? _lastAcceptanceNotice;
    private long _acceptanceStatusVersion, _lastAcceptanceEpoch, _lastAcceptanceInput;
    private long _contextReadAt, _inputVersion, _publicationVersion, _epoch, _enableRequestVersion, _compositionEpoch, _locationIntentVersion;
    private long _readAttempts, _nullReads, _candidateInvalidations, _diagnosticSequence;
    private bool _candidateAbsent, _composing, _writing, _candidateSeen;
    private PendingTab? _pendingTab;
    private EditableContextIdentity? _bindingFailureIdentity, _unsupportedIdentity;
    private string? _unsupportedReason;
    private long _bindingRetryAt, _lastBindingFailureAt;
    private int _bindingFailureCount;
    private int _enabled, _disposed, _updateStopped;
    private sealed record Binding(Process Process, ICandidateObserver Observer, EditableContextIdentity Identity)
    {
        public bool Activated { get; set; }
        public UpdateQuiescenceTracker.Resource? UpdateLifetime { get; set; }
    }
    private sealed record Publication(Binding Binding, CandidateObservation Source, CandidateObservation Published,
        EditableContextSnapshot Context, BaselineAnchor? Anchor, long InputVersion);
    private sealed record PendingTab(Binding Binding, long ModeEpoch, long CompositionEpoch, long InputVersion,
        CancellationToken ModeCancellation, EditableContextSnapshot Context);
    private enum CopyState { None, Copying, Copied, Failed }

    public event Action<CandidateTranslationState>? PreviewChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? TranslationCopied;
    public event Action? CopyPresentationChanged;
    public bool Enabled => Volatile.Read(ref _enabled) == 1 && Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _updateStopped) == 0;
    public CandidateTranslationState State => _coordinator.State;
    public bool CanAcceptCurrent { get { lock (_gate) return !_copyOnly && _publication is { } current && CanAcceptPublicationLocked(current); } }
    public string CurrentAcceptanceHint { get { lock (_gate) return AcceptanceHintLocked(); } }

    public ImeRuntimeController(Func<string, CancellationToken, Task<string>> translate, int? testProcessId = null,
        IReadOnlyCollection<TextInputProfile>? verifiedTextInputProfiles = null,
        Func<Process, string, string, ICandidateObserver>? observerFactory = null,
        bool copyOnly = true, ITranslationClipboardWriter? clipboardWriter = null)
    {
        _testProcessId = testProcessId;
        _copyOnly = copyOnly;
        _clipboardWriter = clipboardWriter ?? (copyOnly ? new WindowsTranslationClipboardWriter() : null);
        _observerFactory = observerFactory ?? ((process, id, runtime) => new CandidateObserverClient(process, id, runtime));
        _contexts = new(verifiedTextInputProfiles ?? [TextInputProfile.WinFormsEdit, TextInputProfile.WinFormsRichEdit]);
        _readContextAsync = _contexts.ReadFocusedAsync;
        _foregroundWindow = GetForegroundWindow;
        _replaceAsync = _contexts.GuardedReplaceAsync;
        _writeDiagnostics = new();
        _contexts.WriteProgress += OnWriteProgress;
        _coordinator = new((candidate, token) => translate(candidate.Candidate, token), ValidateAsync, copyOnly ? null : AcceptAsync);
        _coordinator.Changed += OnStateChanged;
        _coordinator.Faulted += OnCoordinatorFaulted;
        _keyboard = new(TryAcceptTab, OnKeyStroke);
        _pointer = new(OnPointerStroke);
        _disableForUpdateAsync = () => SetEnabledAsync(false);
        Diagnose("constructed");
    }

    public async Task<string?> SetEnabledAsync(bool enabled)
    {
        long request;
        lock (_gate)
        {
            if (_disposed != 0) return "程序正在退出。";
            if (enabled && _updateStopped != 0) return "正在准备更新，请稍后再开启翻译。";
            request = ++_enableRequestVersion;
        }
        Diagnose("mode.request", $"request={request};enable={enabled}");
        if (enabled)
        {
            try
            {
                if (!await _keyboard.Ready.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
                    return IsEnableRequestCurrent(request) ? "无法启动输入监听：" + _keyboard.InstallationError : null;
                if (!await _pointer.Ready.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false) || !_pointer.IsInstalled)
                    return IsEnableRequestCurrent(request) ? "无法启动鼠标位置监听：" + _pointer.InstallationError : null;
                if (!await _contexts.Ready.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
                    return IsEnableRequestCurrent(request) ? "无法启动输入上下文读取。" : null;
            }
            catch (TimeoutException)
            {
                Diagnose("mode.ready-timeout", $"request={request}");
                return IsEnableRequestCurrent(request) ? "输入监听启动超时，未开启。" : null;
            }
        }
        CancellationTokenSource? previous;
        Binding? previousBinding;
        CancellationToken token;
        long epoch;
        lock (_gate)
        {
            if (_disposed != 0) return "程序正在退出。";
            if (enabled && _updateStopped != 0) return "正在准备更新，请稍后再开启翻译。";
            if (request != _enableRequestVersion) return null;
            previous = _pollCancellation;
            _pollCancellation = enabled ? new() : null;
            token = _pollCancellation?.Token ?? default;
            epoch = ++_epoch;
            Interlocked.Exchange(ref _enabled, enabled ? 1 : 0);
            _inputVersion++;
            previousBinding = _binding; _binding = null;
            _context = null; _contextReadAt = 0;
            _bindingFailureIdentity = null; _unsupportedIdentity = null; _unsupportedReason = null;
            _bindingRetryAt = 0; _lastBindingFailureAt = 0; _bindingFailureCount = 0;
            _anchor = null; _publication = null; _candidateAbsent = false; _composing = false;
            CancelCopyLocked();
            _acceptanceStatusVersion++; _lastAcceptanceNotice = null;
            _compositionEpoch++; _candidateSeen = false; CancelPendingTabLocked("mode-change");
            _keyboard.Enabled = enabled && !_copyOnly;
            // These methods only update coordinator state and queue notifications.
            // Keep their order atomic with the runtime epoch and publication changes.
            _coordinator.SetEnabled(enabled);
        }
        _ = CancelPollingAsync(previous);
        ReleaseBinding(previousBinding);
        Diagnose("mode.committed", $"request={request};enable={enabled}");
        if (enabled)
        {
            _ = Task.Run(() => PollAsync(epoch, token));
            if (IsEnableRequestCurrent(request)) Report(_copyOnly
                ? "候选自动翻译并复制已开启；看到已复制后，手动按 Esc，再 Ctrl+V 粘贴。"
                : "中译英模式已开启；请在输入框中开始新一轮微软拼音输入。");
        }
        else if (IsEnableRequestCurrent(request)) Report(_copyOnly
            ? "自动翻译复制已关闭；剪贴板内容保留。"
            : "中译英模式已关闭，Tab 保持原行为。");
        return null;
    }

    public async Task StopForUpdateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed != 0) throw new InvalidOperationException("程序正在退出，无法准备更新。");
            Volatile.Write(ref _updateStopped, 1);
            _updateQuiescence?.Pause();
            ++_enableRequestVersion;
        }
        // Admission is already closed before any asynchronous cancellation.
        await (_disableForUpdateAsync?.Invoke() ?? SetEnabledAsync(false)).ConfigureAwait(false);
        if (_updateQuiescence is { } quiescence)
            await quiescence.PauseAndDrainAsync(cancellationToken).ConfigureAwait(false);
    }

    public void ResumeAfterUpdateFailure()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            _updateQuiescence?.Resume();
            Volatile.Write(ref _updateStopped, 0);
        }
    }

    private bool IsEnableRequestCurrent(long request)
    {
        lock (_gate) return _disposed == 0 && request == _enableRequestVersion;
    }

    private static async Task CancelPollingAsync(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { }
        finally { source.Dispose(); }
    }

    private async Task PollAsync(long epoch, CancellationToken token)
    {
        Diagnose("poll.started", $"pollEpoch={epoch}");
        try
        {
            while (!token.IsCancellationRequested && Enabled)
            {
                var live = await _readContextAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Binding? binding;
                bool changed, bindingStopped;
                lock (_gate)
                {
                    if (epoch != _epoch) return;
                    if (live is not null && _testProcessId is { } allowed && live.FocusProcessId != allowed) live = null;
                    binding = _binding;
                    if (live is not null && binding is not null && EditableContextIdentity.From(live) != binding.Identity)
                    {
                        _locationIntentVersion++;
                        // Revoke A before exposing B's fresh context to the key
                        // hook. Otherwise B's first key could capture an anchor
                        // which BindAsync immediately erases while detaching A.
                        DetachBinding(epoch, binding);
                        binding = null;
                    }
                    bindingStopped = binding is { Activated: true } && !binding.Observer.IsRunning;
                    if (live is not null && _bindingFailureIdentity is not null && EditableContextIdentity.From(live) != _bindingFailureIdentity)
                    {
                        _bindingFailureIdentity = null; _bindingRetryAt = 0;
                        _lastBindingFailureAt = 0; _bindingFailureCount = 0;
                    }
                    changed = _context is not null && (live is null || !EditableContextCapabilities.HasSameTextLocation(_context, live));
                    _context = live;
                    _contextReadAt = Stopwatch.GetTimestamp();
                    _readAttempts++;
                    if (live is null)
                    {
                        _nullReads++;
                        if (_nullReads <= 3 || _nullReads % 10 == 0) Diagnose("poll.read-null", $"reads={_readAttempts};nulls={_nullReads}");
                    }
                    else if (_readAttempts == 1 || changed || binding is null)
                        Diagnose("poll.context", ContextDiagnostic(live));
                    if (_writing)
                    {
                        if (live is null || binding is null || EditableContextIdentity.From(live) != binding.Identity)
                        { _inputVersion++; _publication = null; InvalidateOperationsLocked(); }
                    }
                    else if (changed && _publication is not null && (live is null || !EditableContextCapabilities.HasSameTextLocation(live, _publication.Context)))
                    { _publication = null; InvalidateOperationsLocked(); binding?.Observer.NotifyInputChanged(); }
                    if (!_writing && _pendingTab is not null &&
                        (live is null || !MatchesPendingContext(_pendingTab.Context, live)))
                    {
                        _inputVersion++; _candidateSeen = false;
                        CancelPendingTabLocked("context-changed");
                    }
                    if (!_writing && !_composing && _candidateAbsent && live is not null)
                        _anchor = CreateAnchor(live);
                }
                if (live is not null) UpdateCapabilityStatus(live, epoch);
                if (live is null)
                {
                    if (binding is not null) DetachBinding(epoch, binding);
                }
                else if (bindingStopped && binding is not null && EditableContextIdentity.From(live) == binding.Identity)
                    HandleBindingFailure(epoch, binding.Identity, binding, binding.Observer.Failure ??
                        new(CandidateObserverFailureCode.BridgeExited, "running"));
                else if (CanParticipate(live) && (binding is null || EditableContextIdentity.From(live) != binding.Identity) &&
                    CanAttemptBinding(live, epoch)) _ = BindAsync(live, epoch, token);
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
        catch (Exception error)
        {
            Diagnose("poll.failed", error.GetType().Name);
            lock (_gate)
            {
                if (epoch != _epoch || !Enabled) return;
                _ = SetEnabledAsync(false);
                Report((_copyOnly ? "输入监听已停止，自动翻译复制已关闭：" : "输入监听已停止，中译英模式已关闭：") + error.GetType().Name);
            }
        }
    }

    private async Task BindAsync(EditableContextSnapshot context, long epoch, CancellationToken token)
    {
        IDisposable? operation;
        lock (_gate)
        {
            if (!Enabled || _updateStopped != 0 || epoch != _epoch || token.IsCancellationRequested) return;
            operation = _updateQuiescence?.TryBeginOperation();
            if (_updateQuiescence is not null && operation is null) return;
        }
        using (operation) await BindCoreAsync(context, epoch, token).ConfigureAwait(false);
    }

    private async Task BindCoreAsync(EditableContextSnapshot context, long epoch, CancellationToken token)
    {
        Binding? previous;
        var identity = EditableContextIdentity.From(context);
        lock (_gate)
        {
            if (!Enabled || epoch != _epoch || token.IsCancellationRequested || _context is null ||
                EditableContextIdentity.From(_context) != identity || !CanParticipate(context) ||
                (_bindingFailureIdentity == identity && Stopwatch.GetTimestamp() < _bindingRetryAt)) return;
            previous = _binding;
        }
        // With no prior binding, preserve a tentative anchor captured by the
        // first key between Poll's context read and this helper startup.
        if (previous is not null && !DetachBinding(epoch, previous)) return;
        Diagnose("bind.begin", ContextDiagnostic(context));
        Process? process = null;
        ICandidateObserver? observer = null;
        Binding? provisional = null;
        bool ownedByRuntime = false, activated = false;
        try
        {
            process = Process.GetProcessById(context.FocusProcessId);
            observer = _observerFactory(process, context.AutomationId, context.FocusRuntimeId);
            var binding = new Binding(process, observer, identity);
            provisional = binding;
            binding.UpdateLifetime = _updateQuiescence?.Register(() => StopDetachedBindingAsync(binding));
            observer.Invalidated += _ => OnCandidateInvalidated(binding);
            observer.FocusInvalidated += () => OnFocusInvalidated(binding);
            observer.Observed += candidate => { _ = ObserveAsync(binding, candidate); };
            observer.CandidatesCleared += version => { _ = ObserveClearAsync(binding, version); };
            observer.Status += status =>
            {
                if (status.StartsWith("IME_DIAG", StringComparison.Ordinal)) Diagnose("observer", status);
                // Only the current Bind/Poll owner reports lifecycle failures.
                // A disposed helper's queued status must not overwrite a new editor.
            };
            lock (_gate)
            {
                if (!Enabled || epoch != _epoch || token.IsCancellationRequested || _binding is not null ||
                    _context is null || EditableContextIdentity.From(_context) != identity)
                    throw new OperationCanceledException(token);
                _binding = binding;
                ownedByRuntime = true;
            }
            // The provisional binding owns both resources from this point.
            // A concurrent detach/mode change will release them itself.
            process = null; observer = null;
            Diagnose("bind.provisional");
            Diagnose("bind.wait-ready");
            if (!await binding.Observer.Ready.WaitAsync(TimeSpan.FromSeconds(7), token).ConfigureAwait(false))
            {
                HandleBindingFailure(epoch, identity, binding, binding.Observer.Failure ??
                    new(CandidateObserverFailureCode.ObserverInitializationFailed, "ready-await"));
                return;
            }
            lock (_gate)
            {
                if (!Enabled || epoch != _epoch || token.IsCancellationRequested || !ReferenceEquals(_binding, binding))
                    throw new OperationCanceledException(token);
                if (!binding.Observer.SetEnabled(true))
                {
                    HandleBindingFailure(epoch, identity, binding, binding.Observer.Failure ??
                        new(CandidateObserverFailureCode.ObserverInitializationFailed, "enable"));
                    return;
                }
                binding.Activated = true;
                _unsupportedIdentity = null; _unsupportedReason = null;
                activated = true;
                Report(_copyOnly ? "候选读取已连接，正在等待首项翻译并复制。" :
                    _context is { CanReplace: false, CanPreview: true } ? PreviewOnlyStatus : "候选读取已连接，正在等待微软拼音候选。");
            }
            Diagnose("bind.attached");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            HandleBindingFailure(epoch, identity, provisional,
                provisional?.Observer.Failure ?? CandidateObserverFailure.FromException(error, "binding"));
        }
        finally
        {
            if (ownedByRuntime && !activated && provisional is not null)
                DetachBinding(epoch, provisional);
            if (observer is not null && provisional is not null) ReleaseDetachedBinding(provisional);
            else { observer?.Dispose(); process?.Dispose(); }
        }
    }

    private bool CanAttemptBinding(EditableContextSnapshot context, long epoch)
    {
        lock (_gate) return Enabled && epoch == _epoch &&
            (_bindingFailureIdentity != EditableContextIdentity.From(context) || Stopwatch.GetTimestamp() >= _bindingRetryAt);
    }

    private bool CanParticipate(EditableContextSnapshot context) => !context.IsPassword &&
        (_copyOnly ? context.CanPreview : context.CanPreview || context.CanReplace);

    private void UpdateCapabilityStatus(EditableContextSnapshot context, long epoch)
    {
        lock (_gate)
        {
            if (!Enabled || epoch != _epoch || _writing || !ReferenceEquals(_context, context)) return;
            var identity = EditableContextIdentity.From(context);
            if (_copyOnly)
            {
                if (context.CanPreview && !context.IsPassword)
                {
                    _unsupportedIdentity = null; _unsupportedReason = null;
                }
                else if (_unsupportedIdentity != identity || _unsupportedReason != context.CapabilityReason)
                {
                    _unsupportedIdentity = identity; _unsupportedReason = context.CapabilityReason;
                    Report("当前控件无法读取微软拼音候选；自动复制暂不可用。");
                }
                return;
            }
            if (context.CanReplace)
            {
                bool restored = _unsupportedIdentity == identity;
                _unsupportedIdentity = null; _unsupportedReason = null;
                if (restored && _binding is { Activated: true } binding && binding.Observer.IsRunning)
                    Report("输入框已恢复可核对状态，正在等待微软拼音候选。");
            }
            else if (_unsupportedIdentity != identity || _unsupportedReason != CapabilityStatusKey(context))
            {
                _unsupportedIdentity = identity; _unsupportedReason = CapabilityStatusKey(context);
                Report(context.CanPreview ? PreviewOnlyStatus : UnsupportedStatus(context));
            }
        }
    }

    private static string CapabilityStatusKey(EditableContextSnapshot context) =>
        (context.CanPreview ? "PreviewOnly:" : "Unsupported:") + context.CapabilityReason;

    private static string UnsupportedStatus(EditableContextSnapshot context) => context.CapabilityReason switch
    {
        "ReadOnly" => "此输入框为只读，或无法确认写入权限；可使用划词或截图翻译。",
        "TextPatternMissing" => "此输入框未提供可核对的正文和光标接口；可使用截图翻译。",
        "SelectionUnavailable" => "暂时无法核对输入光标，请先取消选区或完成当前组合。",
        "TextRepresentationsDiffer" => "此输入框的正文接口结果不一致，暂不写入译文。",
        "UnverifiedControlProfile" => "此类编辑器尚未适配 Tab 写入；可使用划词或截图翻译。",
        _ => UnsupportedContextStatus
    };

    private void HandleBindingFailure(long epoch, EditableContextIdentity identity, Binding? binding, CandidateObserverFailure failure)
    {
        int retrySeconds;
        lock (_gate)
        {
            if (!Enabled || epoch != _epoch ||
                (binding is not null ? !ReferenceEquals(_binding, binding) : _binding is not null) ||
                (_context is not null && EditableContextIdentity.From(_context) != identity)) return;
            if (binding is null && _context is null) return;
            if (binding is not null && !DetachBinding(epoch, binding)) return;
            long now = Stopwatch.GetTimestamp();
            if (_bindingFailureIdentity != identity || _lastBindingFailureAt == 0 ||
                Stopwatch.GetElapsedTime(_lastBindingFailureAt, now) > TimeSpan.FromSeconds(30)) _bindingFailureCount = 0;
            _bindingFailureIdentity = identity;
            _lastBindingFailureAt = now;
            _bindingFailureCount = Math.Min(5, _bindingFailureCount + 1);
            retrySeconds = Math.Min(15, 1 << (_bindingFailureCount - 1));
            _bindingRetryAt = now + retrySeconds * Stopwatch.Frequency;
        }
        Diagnose("bind.failed", $"code={failure.Code};stage={failure.Stage};error={failure.ErrorType};hresult={failure.HResult};nativeError={failure.NativeErrorCode};childExitCode={failure.ChildExitCode};retrySeconds={retrySeconds}");
        lock (_gate)
        {
            if (Enabled && epoch == _epoch && _binding is null && _bindingFailureIdentity == identity &&
                (_context is null || EditableContextIdentity.From(_context) == identity))
                Report(failure.ToUserMessage() + $"（阶段：{failure.Stage}，代码：{failure.Code}）{retrySeconds} 秒后重试；" +
                    (_copyOnly ? "尚未复制译文。" : "Tab 保持原行为。"));
        }
    }

    private bool DetachBinding(long? expectedEpoch = null, Binding? expectedBinding = null)
    {
        Binding? old;
        lock (_gate)
        {
            if (expectedEpoch is { } epoch && (!Enabled || epoch != _epoch)) return false;
            if (expectedBinding is not null && !ReferenceEquals(_binding, expectedBinding)) return false;
            old = _binding; _binding = null;
            _inputVersion++; _anchor = null; _publication = null;
            _candidateAbsent = false; _composing = false;
            _compositionEpoch++; _candidateSeen = false; CancelPendingTabLocked("binding-change");
            InvalidateOperationsLocked();
        }
        ReleaseBinding(old);
        return true;
    }

    private void ReleaseBinding(Binding? old)
    {
        if (old is null) return;
        lock (_gate)
        {
            // A mode toggle may detach the active binding after SetValue. Keep
            // its focus observer alive until caret-only completion finishes.
            if (_disposed == 0 && _updateStopped == 0 && ReferenceEquals(old, _writingBinding))
            { _deferredRelease = old; return; }
        }
        ReleaseDetachedBinding(old);
    }
    private static void ReleaseDetachedBinding(Binding? old)
    {
        if (old is null) return;
        _ = ObserveBindingStopAsync(old.UpdateLifetime?.StopAsync() ?? Task.Run(() => StopDetachedBindingAsync(old)));
    }

    private static async Task StopDetachedBindingAsync(Binding binding)
    {
        try
        {
            if (binding.Observer is ICandidateObserverShutdown asynchronous)
                await asynchronous.StopAsync().ConfigureAwait(false);
            else binding.Observer.Dispose();
        }
        finally { binding.Process.Dispose(); }
    }

    private static async Task ObserveBindingStopAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch (Exception) { }
    }

    private void OnFocusInvalidated(Binding binding)
    {
        lock (_gate)
        {
            bool activeBinding = ReferenceEquals(binding, _binding);
            if (!activeBinding && !ReferenceEquals(binding, _writingBinding)) return;
            _locationIntentVersion++;
            if (!activeBinding) { Diagnose("focus.invalidated-detached-writer"); return; }
            _inputVersion++;
            _publication = null; _anchor = null; _context = null;
            _candidateAbsent = false; _composing = false;
            _compositionEpoch++; _candidateSeen = false; CancelPendingTabLocked("focus-change");
            // Focus loss is external evidence even during our own SetValue/Select.
            InvalidateOperationsLocked();
        }
        Diagnose("focus.invalidated");
    }

    private void OnCandidateInvalidated(Binding binding)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(binding, _binding)) return;
            _candidateInvalidations++;
            if (_writing)
            {
                if (_candidateInvalidations <= 5 || _candidateInvalidations % 10 == 0) Diagnose("candidate.invalidated-during-write");
                return;
            }
            _candidateAbsent = false;
            _publication = null;
            InvalidateOperationsLocked();
            if (_candidateInvalidations <= 5 || _candidateInvalidations % 10 == 0) Diagnose("candidate.invalidated");
        }
    }

    private async Task ObserveClearAsync(Binding binding, long version)
    {
        Diagnose("clear.received", $"source={version}");
        try
        {
            long input;
            lock (_gate)
            {
                if (!ReferenceEquals(binding, _binding) || _writing) { Diagnose("clear.rejected-local"); return; }
                input = _inputVersion;
            }
            var live = await _readContextAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                if (!Enabled || _writing || !ReferenceEquals(binding, _binding) || input != _inputVersion || live is null ||
                    !binding.Observer.IsCurrent(version) || EditableContextIdentity.From(live) != binding.Identity)
                { Diagnose("clear.stale-or-read-null", $"source={version};readNull={live is null}"); return; }
                _context = live; _contextReadAt = Stopwatch.GetTimestamp();
                if (_composing)
                {
                    // A briefly absent candidate panel is not an ended composition.
                    // In particular, do not promote the first raw pinyin letter to a baseline.
                    _candidateAbsent = false; _publication = null;
                    InvalidateOperationsLocked();
                    Diagnose("clear.deferred-composing", $"anchorMatches={_anchor is not null && MatchesAnchor(_anchor, live)}");
                    return;
                }
                _anchor = CreateAnchor(live); _candidateAbsent = true; _composing = false; _publication = null;
                _compositionEpoch++; _candidateSeen = false; CancelPendingTabLocked("composition-ended");
                InvalidateOperationsLocked();
                Diagnose(_anchor is null ? "clear.anchor-unavailable" : "clear.baseline-ready", ContextDiagnostic(live));
            }
            Report(_copyOnly ? "正在等待微软拼音候选；译文成功后将自动复制。" : live.CanReplace ? "中译英已就绪：输入微软拼音，按 Tab 接受英文。" :
                live.CanPreview ? PreviewOnlyStatus : UnsupportedStatus(live));
        }
        catch (Exception error) { Diagnose("clear.failed", error.GetType().Name); if (Enabled) Report("输入状态读取失败：" + error.GetType().Name); }
    }

    private async Task ObserveAsync(Binding binding, CandidateObservation source)
    {
        Diagnose("observe.received", $"source={source.Version};items={source.VisiblePage.Count}");
        try
        {
            BaselineAnchor? anchor;
            long input;
            lock (_gate)
            {
                if (!Enabled || _writing || !ReferenceEquals(binding, _binding)) { Diagnose("observe.rejected-local"); return; }
                anchor = _anchor; input = _inputVersion;
            }
            var live = await _readContextAsync(CancellationToken.None).ConfigureAwait(false);
            if (live is null) { Diagnose("observe.read-null"); return; }
            if (!CanParticipate(live))
            {
                Diagnose("observe.unsupported-context");
                Report(_copyOnly ? "当前控件无法读取微软拼音候选；自动复制暂不可用。" : UnsupportedStatus(live));
                return;
            }
            // A candidate can be previewed without a trustworthy insertion baseline.
            // Preserve the pre-key anchor as a hypothesis, even if it does not
            // match pre-edit. The writer must prove the post-Esc baseline before
            // any insertion, including when this hypothesis is missing or wrong.
            if (!_copyOnly && (anchor is null || !MatchesAnchor(anchor, live))) Diagnose("observe.baseline-needs-verification", ContextDiagnostic(live));
            if (!await binding.Observer.ValidateAsync(source).ConfigureAwait(false)) { Diagnose("observe.source-rejected", $"source={source.Version}"); return; }
            CandidateObservation published;
            lock (_gate)
            {
                if (!Enabled || _writing || input != _inputVersion || !ReferenceEquals(binding, _binding) ||
                    !binding.Observer.IsCurrent(source.Version) || source.FocusRuntimeId != live.FocusRuntimeId)
                { Diagnose("observe.stale-after-validation", $"source={source.Version};capturedInput={input}"); return; }
                published = source with { Version = ++_publicationVersion };
                CancelCopyLocked();
                _context = live; _contextReadAt = Stopwatch.GetTimestamp(); _candidateAbsent = false; _composing = true;
                var publication = new Publication(binding, source, published, live, anchor, input);
                _publication = publication;
                // Observe queues all callbacks; make it atomic with runtime invalidation.
                bool accepted = _coordinator.Observe(published);
                if (!accepted) _publication = null;
                else
                {
                    _candidateSeen = true;
                    var pending = _pendingTab;
                    if (pending is not null)
                    {
                        bool sameRequest = IsPendingTabCurrentLocked(pending) && pending.InputVersion == input &&
                            MatchesPendingContext(pending.Context, live);
                        _pendingTab = null;
                        bool queued = sameRequest && CanAcceptPublicationLocked(publication) && _coordinator.TryRequestAccept();
                        Diagnose(queued ? "tab.pending-joined" : "tab.pending-rejected");
                    }
                }
                Diagnose(accepted ? "observe.published" : "observe.coordinator-rejected", $"source={source.Version}");
            }
        }
        catch (Exception error) { Diagnose("observe.failed", error.GetType().Name); if (Enabled) Report("候选更新失败：" + error.GetType().Name); }
    }

    private static BaselineAnchor? CreateAnchor(EditableContextSnapshot live)
    {
        return EditableContextAdapter.TryCreateAnchor(live, out var anchor) ? anchor : null;
    }
    private static bool MatchesAnchor(BaselineAnchor anchor, EditableContextSnapshot live) =>
        anchor.TryMatch(live, out _);

    private bool IsManagedPublicationCurrentLocked(Publication current) => Enabled && ReferenceEquals(_publication, current) &&
        ReferenceEquals(_binding, current.Binding) && current.InputVersion == _inputVersion;

    private bool CanAcceptPublicationLocked(Publication current) => IsManagedPublicationCurrentLocked(current) &&
        _coordinator.State.Observation?.Version == current.Published.Version && CanWrite(current.Context) &&
        _context is { } live && EditableContextCapabilities.HasSameTextLocation(current.Context, live, requireWritable: true) &&
        current.Binding.Observer.IsCurrent(current.Source.Version);

    private static bool CanWrite(EditableContextSnapshot context) => context.CanReplace &&
        EditableContextCapabilities.HasSameTextLocation(context, context, requireWritable: true);

    private void OnKeyStroke(KeyStroke key)
    {
        if (!key.IsKeyDown) return;
        Binding? binding;
        lock (_gate)
        {
            if (_disposed != 0) return;
            // The hook continues observing while disabled. A mode toggle is not
            // itself a location change, but later navigation/edit input still is.
            if (IsLocationIntent(key)) _locationIntentVersion++;
            if (!Enabled) return;
            binding = _binding;
            if (_context is null || key.ForegroundWindow != _context.ForegroundWindow ||
                (binding is null && Stopwatch.GetElapsedTime(_contextReadAt) >= TimeSpan.FromMilliseconds(600)))
            {
                _inputVersion++; _publication = null; _anchor = null; _composing = false;
                _compositionEpoch++; _candidateSeen = false; CancelPendingTabLocked("key-context-change");
                InvalidateOperationsLocked(); return;
            }
            CancelPendingTabLocked("physical-key");
            uint vk = key.VirtualKey;
            bool letter = (vk is >= 0x41 and <= 0x5A || vk == 0xDE) && (key.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Windows)) == 0;
            bool candidateNavigation = vk is 0x21 or 0x22 or 0x26 or 0x28;
            bool backspace = vk == 0x08;
            if (letter && !_composing)
            {
                // The candidate host may not exist before the first pinyin letter.
                // This is a tentative baseline, not proof that a composition ended:
                // the acceptor must cancel composition and verify this exact baseline.
                bool fresh = Stopwatch.GetElapsedTime(_contextReadAt) < TimeSpan.FromMilliseconds(600);
                _anchor = fresh ? CreateAnchor(_context) : null;
                _composing = true;
                _compositionEpoch++; _candidateSeen = false;
                Diagnose(_anchor is null ? "key.baseline-unavailable" : "key.baseline-captured", $"fresh={fresh}");
            }
            else if (!letter && !candidateNavigation && !(backspace && _composing))
            { _anchor = null; _composing = false; _compositionEpoch++; _candidateSeen = false; }
            _inputVersion++; _candidateAbsent = false; _publication = null;
            InvalidateOperationsLocked();
            // Client invalidation is local and pipe writes are queued. Make the
            // client sequence barrier atomic with the runtime's input generation.
            binding?.Observer.NotifyInputChanged();
        }
    }

    private static bool IsLocationIntent(KeyStroke key)
    {
        uint vk = key.VirtualKey;
        if (vk is 0x08 or 0x09 or 0x0D or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E) return true;
        bool control = (key.Modifiers & KeyModifiers.Control) != 0;
        bool altOrWindows = (key.Modifiers & (KeyModifiers.Alt | KeyModifiers.Windows)) != 0;
        if (control && !altOrWindows && (vk is 0x41 or 0x56 or 0x58 or 0x59 or 0x5A)) return true; // selection/paste/cut/redo/undo
        if (control || altOrWindows) return false; // Mode shortcuts such as Ctrl+Alt+T do not move the caret.
        return vk == 0x20 || vk is >= 0x30 and <= 0x5A || vk is >= 0x60 and <= 0x6F || vk is >= 0xBA and <= 0xE2;
    }

    private void OnPointerStroke(PointerStroke pointer)
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            _locationIntentVersion++;
            _inputVersion++;
            _publication = null; _anchor = null; _context = null; _contextReadAt = 0;
            _candidateAbsent = false; _composing = false; _candidateSeen = false; _compositionEpoch++;
            CancelPendingTabLocked("pointer-intent");
            InvalidateOperationsLocked();
            _binding?.Observer.NotifyInputChanged();
            Diagnose("pointer.intent", pointer.Intent.ToString());
        }
    }

    private bool TryAcceptTab(nint foreground)
    {
        if (_copyOnly) return false;
        lock (_gate)
        {
            var current = _publication;
            if (!Enabled) return false;
            if (current is not null)
            {
                if (!IsManagedPublicationCurrentLocked(current) || current.Context.ForegroundWindow != foreground)
                    return RejectTabLocked("LocationChanged", "输入位置已变化，本次 Tab 接受已取消。", current.Context);
                var state = _coordinator.State;
                // Merge both keyboard repeat and a second complete physical press.
                if (state.Observation?.Version == current.Published.Version && state.AcceptRequested && _writing) return true;
                if (!CanWrite(current.Context) || _context is null || !CanWrite(_context))
                    return RejectTabLocked("CapabilityUnavailable", CapabilityAcceptanceHint(_context ?? current.Context), _context ?? current.Context);
                if (!current.Binding.Observer.IsCurrent(current.Source.Version))
                {
                    // The client revokes evidence immediately, while its queued
                    // Invalidated callback may not have cleared this publication
                    // yet. Cancel the old operation and wait for fresh evidence.
                    if (!EditableContextCapabilities.HasSameTextLocation(current.Context, _context, requireWritable: true))
                        return RejectTabLocked("ContextChanged", "正文或光标已变化，本次 Tab 接受已取消。", _context);
                    _publication = null;
                    InvalidateOperationsLocked();
                    return QueuePendingTabLocked(foreground);
                }
                if (!CanAcceptPublicationLocked(current))
                    return RejectTabLocked("PublicationUnavailable", "当前候选或译文已失效，请继续输入后重试。", _context);
                if (state.AcceptRequested) return true;
                if (_writing) return RejectTabLocked("WriterBusy", "上一条输入仍在核对，请稍候。", _context);
                bool accepted = _coordinator.TryRequestAccept();
                if (!accepted) return RejectTabLocked("RequestUnavailable", "当前候选尚未就绪，Tab 保持原作用。", _context);
                RecordWrite("tab", current.Anchor is null ? "RequestedWithoutAnchor" : "Requested", current.Context, current.Anchor is not null);
                QueueAcceptanceStatusLocked(current.Anchor is null || !MatchesAnchor(current.Anchor, current.Context)
                    ? "已接受 Tab；将先取消拼音并核对实际正文，再决定是否输入。" : "已接受 Tab，正在等待译文并核对输入位置。");
                return true;
            }
            return QueuePendingTabLocked(foreground);
        }
    }

    private bool QueuePendingTabLocked(nint foreground)
    {
        if (_writing) return RejectTabLocked("WriterBusy", "上一条输入仍在核对，请稍候。", _context);
        if (_context is not { } context || context.ForegroundWindow != foreground)
            return RejectTabLocked("ContextUnavailable", "尚未取得当前输入框的位置，Tab 保持原作用。", _context);
        if (!CanWrite(context)) return RejectTabLocked("CapabilityUnavailable", CapabilityAcceptanceHint(context), context);
        if (!_composing || !_candidateSeen || _binding is null)
            return RejectTabLocked("CandidateNotSeen", "尚未识别到本次拼音候选，Tab 保持原作用。", context);
        if (Stopwatch.GetElapsedTime(_contextReadAt) >= TimeSpan.FromMilliseconds(600))
            return RejectTabLocked("ContextExpired", "输入位置尚未更新，Tab 保持原作用；请稍后重试。", context);
        if (_binding.Identity != EditableContextIdentity.From(context))
            return RejectTabLocked("BindingChanged", "输入框已变化，正在连接新的候选读取。", context);
        if (_pendingTab is { } existing && IsPendingTabCurrentLocked(existing)) return true;
        var pending = new PendingTab(_binding, _epoch, _compositionEpoch, _inputVersion, _pollCancellation?.Token ?? default, context);
        _pendingTab = pending;
        Diagnose("tab.pending-queued");
        RecordWrite("tab", "PendingFreshCandidate", context, _anchor is not null);
        QueueAcceptanceStatusLocked("候选正在更新，已排队等待最新候选；输入或位置变化会取消。");
        _ = Task.Run(() => ExpirePendingTabAsync(pending));
        return true;
    }

    private bool RejectTabLocked(string code, string hint, EditableContextSnapshot? context)
    {
        RecordWrite("tab-rejected", code, context, _anchor is not null);
        RememberAcceptanceNoticeLocked(hint);
        QueueAcceptanceStatusLocked(hint, completedGesture: true);
        return false;
    }

    private void QueueAcceptanceStatusLocked(string hint, bool completedGesture = false)
    {
        long epoch = _epoch, input = _inputVersion;
        long notice = ++_acceptanceStatusVersion;
        var binding = _binding;
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                if (Enabled && epoch == _epoch && notice == _acceptanceStatusVersion &&
                    (completedGesture || (input == _inputVersion && ReferenceEquals(binding, _binding)))) Report(hint);
            }
        });
    }

    private static bool MatchesPendingContext(EditableContextSnapshot previous, EditableContextSnapshot live)
    {
        if (!CanWrite(previous) || !CanWrite(live) || EditableContextIdentity.From(previous) != EditableContextIdentity.From(live)) return false;
        if (EditableContextCapabilities.HasSameTextLocation(previous, live, requireWritable: true)) return true;
        // The last physical key can still be arriving when Tab is pressed. Only
        // an exact ASCII-pinyin insertion at the cached caret may join that wait;
        // this is a location fence, never a trusted writer baseline.
        if (!EditableContextCapabilities.TryGetInsertionCaret(previous, out int beforeCaret) ||
            !EditableContextCapabilities.TryGetInsertionCaret(live, out int afterCaret)) return false;
        return new BaselineAnchor(previous.PlainText, beforeCaret).TryMatch(live.PlainText, afterCaret, afterCaret, out _);
    }

    private void RememberAcceptanceNoticeLocked(string notice)
    {
        _lastAcceptanceNotice = notice;
        _lastAcceptanceEpoch = _epoch;
        _lastAcceptanceInput = _inputVersion;
        _acceptanceStatusVersion++;
    }

    private string? CurrentAcceptanceNoticeLocked() => _lastAcceptanceEpoch == _epoch && _lastAcceptanceInput == _inputVersion
        ? _lastAcceptanceNotice : null;

    private bool IsPendingTabCurrentLocked(PendingTab pending) => Enabled && ReferenceEquals(_pendingTab, pending) &&
        ReferenceEquals(_binding, pending.Binding) && _epoch == pending.ModeEpoch && _compositionEpoch == pending.CompositionEpoch &&
        _inputVersion == pending.InputVersion && !pending.ModeCancellation.IsCancellationRequested;

    private void CancelPendingTabLocked(string reason)
    {
        if (_pendingTab is null) return;
        _pendingTab = null;
        Diagnose("tab.pending-cancelled", reason);
    }

    private async Task ExpirePendingTabAsync(PendingTab pending)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), pending.ModeCancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        lock (_gate)
        {
            if (!IsPendingTabCurrentLocked(pending)) return;
            _pendingTab = null;
            _candidateSeen = false;
            Diagnose("tab.pending-timeout");
        }
        // Never replay a suppressed Tab after the user's target may have changed.
        Report("最新候选未及时就绪，本次 Tab 接受已取消；请重新输入或重试。");
    }

    private async Task<bool> ValidateAsync(CandidateObservation candidate, CancellationToken token)
    {
        Publication? current;
        lock (_gate) current = _publication;
        if (current is null || current.Published.Version != candidate.Version || !IsPublicationCurrent(current))
        { Diagnose("validate.publication-stale"); return false; }
        var live = await _readContextAsync(token).ConfigureAwait(false);
        if (live is null || !EditableContextCapabilities.HasSameTextLocation(live, current.Context) || !CanParticipate(live))
        { Diagnose("validate.context-mismatch", $"readNull={live is null}"); return false; }
        bool valid = await current.Binding.Observer.ValidateAsync(current.Source, token).ConfigureAwait(false) && IsPublicationCurrent(current);
        Diagnose(valid ? "validate.passed" : "validate.source-stale");
        return valid;
    }
    private bool IsPublicationCurrent(Publication current)
    {
        lock (_gate) return Enabled && ReferenceEquals(_publication, current) && current.InputVersion == _inputVersion &&
            ReferenceEquals(_binding, current.Binding) && _foregroundWindow() == current.Context.ForegroundWindow;
    }

    private async Task<bool> AcceptAsync(CandidateObservation candidate, string translation, CancellationToken token)
    {
        Publication? current;
        BaselineAnchor? anchor;
        long locationIntent, acceptedEpoch;
        lock (_gate)
        {
            current = _publication;
            if (current is null || current.Published.Version != candidate.Version || !CanAcceptPublicationLocked(current) ||
                !IsPublicationCurrent(current))
            {
                RecordWrite("accept-rejected", "PublicationOrCapabilityChanged", current?.Context, current?.Anchor is not null);
                RememberAcceptanceNoticeLocked("候选、正文或光标已变化，本次未进入输入或剪贴板阶段。");
                return false;
            }
            anchor = current.Anchor;
            _writing = true;
            _writingBinding = current.Binding;
            _writingPublication = current;
            _writeProgressHint = "正在取消拼音并核对输入位置…";
            _lastAcceptanceNotice = null;
            locationIntent = _locationIntentVersion;
            acceptedEpoch = _epoch;
        }
        Diagnose("accept.begin");
        RecordWrite("accept", anchor is null ? "BeginWithoutAnchor" : "BeginWithHypothesis", current.Context, anchor is not null);
        try
        {
            var result = await _replaceAsync(current.Context, anchor, translation, token,
                () => IsPublicationCurrent(current),
                () => MayFinishCaret(current.Context, locationIntent)).ConfigureAwait(false);
            lock (_gate)
            {
                _candidateSeen = false; CancelPendingTabLocked("accept-completed");
                if (result.Snapshot is { } after && result.Succeeded && IsPublicationCurrent(current))
                {
                    _context = after; _contextReadAt = Stopwatch.GetTimestamp();
                    _anchor = CreateAnchor(after); _candidateAbsent = true; _composing = false;
                }
                else if (result.WriteAttempted) { _anchor = null; _candidateAbsent = false; _composing = false; }
            }
            Diagnose("accept.result", $"success={result.Succeeded};attempted={result.WriteAttempted};written={result.TextWasWritten};reason={result.Reason}");
            RecordWrite("result", result.Reason, current.Context, anchor is not null);
            string status = result.Succeeded ? "英文已输入；可以继续输入。" : result.WriteAttempted
                ? "输入结果未完整复核，已停止重试，请核对当前文字。" : "输入位置已变化，已取消本次接受。";
            if (!string.IsNullOrWhiteSpace(result.UserNotice)) status += " " + result.UserNotice;
            lock (_gate)
            {
                if (Enabled && _epoch == acceptedEpoch && _inputVersion == current.InputVersion)
                {
                    RememberAcceptanceNoticeLocked(status);
                    Report(status);
                }
            }
            return result.Succeeded;
        }
        finally
        {
            Binding? deferred;
            lock (_gate)
            {
                _writing = false; _writingBinding = null; _writingPublication = null; _writeProgressHint = null;
                deferred = _deferredRelease; _deferredRelease = null;
            }
            ReleaseDetachedBinding(deferred);
        }
    }

    private bool MayFinishCaret(EditableContextSnapshot original, long locationIntent)
    {
        lock (_gate) return _disposed == 0 && _locationIntentVersion == locationIntent &&
            _foregroundWindow() == original.ForegroundWindow;
    }

    private string AcceptanceHintLocked()
    {
        if (_copyOnly) return CopyHintLocked();
        if (!Enabled) return "中译英已关闭，Tab 保持原作用。";
        if (_writing && _writingPublication is { } writing && IsManagedPublicationCurrentLocked(writing))
            return _writeProgressHint ?? "正在核对输入位置并输入英文…";
        if (_pendingTab is { } pending && IsPendingTabCurrentLocked(pending))
            return "正在等待最新候选，输入或位置变化会取消。";
        var current = _publication;
        var context = _context ?? current?.Context;
        if (context is null) return "尚未取得当前输入框的位置，Tab 保持原作用。";
        if (!CanWrite(context)) return CapabilityAcceptanceHint(context);
        if (current is null || !IsManagedPublicationCurrentLocked(current)) return "尚未取得当前有效候选，Tab 保持原作用。";
        if (!current.Binding.Observer.IsCurrent(current.Source.Version)) return "候选正在更新，Tab 可等待最新候选。";
        var state = _coordinator.State;
        if (state.Observation?.Version != current.Published.Version)
            return CurrentAcceptanceNoticeLocked() ?? "当前候选或译文已失效，请继续输入后重试。";
        if (state.IsAccepting) return "正在核对输入位置并输入英文…";
        if (state.AcceptRequested) return "正在等待译文，输入或位置变化会取消。";
        return current.Anchor is null || !MatchesAnchor(current.Anchor, current.Context)
            ? "Tab 输入英文 · 将先核对拼音取消结果" : "Tab 输入英文 · Esc 取消";
    }

    private static string CapabilityAcceptanceHint(EditableContextSnapshot context) =>
        PreviewOnlyStatus + " " + UnsupportedStatus(context);

    private void RecordWrite(string stage, string code, EditableContextSnapshot? context = null, bool hasAnchor = false) =>
        _writeDiagnostics?.Record(stage, code, context, hasAnchor);

    private void OnWriteProgress(ImeWriteProgress progress)
    {
        CandidateTranslationState? state = null;
        lock (_gate)
        {
            var current = _writingPublication;
            RecordWrite(progress.Stage, progress.Code, current?.Context ?? _context, current?.Anchor is not null);
            if (current is null || !_writing || !IsManagedPublicationCurrentLocked(current)) return;
            _writeProgressHint = progress.Stage switch
            {
                "cancel" => "正在取消拼音组合…",
                "baseline" => "正在核对取消后的正文和光标…",
                "clipboard" => "正在准备译文并请求粘贴…",
                "clipboard-restore" => "正在恢复原剪贴板内容…",
                "preflight" => "正在检查候选和输入位置…",
                "insert" or "insertion" => "正在准备译文并请求输入…",
                "verify" or "verification" => "正在核对输入结果…",
                "completed" => "英文输入已完成。",
                "rejected" => "输入核验未通过，已停止自动输入；请查看状态提示。",
                _ => "正在处理 Tab 输入…"
            };
            _acceptanceStatusVersion++;
            Report(_writeProgressHint);
            state = _coordinator.State;
        }
        if (state is not null)
        {
            try { PreviewChanged?.Invoke(state); } catch (Exception) { }
        }
    }

    private void InvalidateOperationsLocked()
    {
        CancelCopyLocked();
        _coordinator.Invalidate();
    }

    private void CancelCopyLocked()
    {
        var ticket = _copyTicket;
        _copyTicket = null;
        _copyState = CopyState.None;
        _copyFailureReason = null;
        if (ticket is not null) _ = ObserveCopyCancellationAsync(ticket.CancelAsync());
        // Successful copies are permanent. Invalidation never edits/restores the clipboard.
    }

    private async Task ObserveCopyCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception) { RecordWrite("copy", "CancellationCallbackFailed"); }
    }

    private CopyTicket? PrepareAutoCopyLocked()
    {
        if (!_copyOnly || !Enabled) return null;
        var state = _coordinator.State;
        var publication = _publication;
        if (publication is null || state.Observation?.Version != publication.Published.Version ||
            state.IsWaiting || string.IsNullOrWhiteSpace(state.Translation)) return null;
        if (_copyTicket is { } previous && ReferenceEquals(previous.Publication, publication) && previous.Generation == state.Generation)
            return null; // Notifications/progress refreshes cannot copy the same publication twice.
        CancelCopyLocked();
        var ticket = new CopyTicket(publication, state.Generation, _epoch, _inputVersion, state.Translation,
            _pollCancellation?.Token ?? default);
        _copyTicket = ticket;
        if (!IsCopyCurrentLocked(ticket)) { CancelCopyLocked(); return null; }
        _copyState = CopyState.Copying;
        RecordWrite("copy", "Queued", publication.Context);
        return ticket;
    }

    private bool IsCopyCurrentLocked(CopyTicket ticket) => _copyOnly && Enabled &&
        ReferenceEquals(_copyTicket, ticket) && !ticket.Token.IsCancellationRequested &&
        ticket.Epoch == _epoch && ticket.InputVersion == _inputVersion && IsManagedPublicationCurrentLocked(ticket.Publication) &&
        ticket.Publication.Context.CanPreview && !ticket.Publication.Context.IsPassword &&
        _context is { CanPreview: true } context && EditableContextCapabilities.HasSameTextLocation(context, ticket.Publication.Context) &&
        _coordinator.State.Generation == ticket.Generation &&
        _coordinator.State.Observation?.Version == ticket.Publication.Published.Version &&
        ticket.Publication.Binding.Observer.IsCurrent(ticket.Publication.Source.Version) &&
        _foregroundWindow() == ticket.Publication.Context.ForegroundWindow;

    private bool IsCopyCurrent(CopyTicket ticket) { lock (_gate) return IsCopyCurrentLocked(ticket); }

    private async Task RunAutoCopyAsync(CopyTicket ticket)
    {
        bool retainCurrentState = false;
        try
        {
            try
            {
                if (!IsCopyCurrent(ticket)) return;
                PublishCopyPresentation(ticket);
                var writer = _clipboardWriter;
                var result = writer is null
                    ? new TranslationClipboardCopyResult(false, false, "WriterUnavailable")
                    : await AwaitCopyAsync(writer.CopyAsync(ticket.Translation, () => IsCopyCurrent(ticket), ticket.Token), ticket.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!IsCopyCurrentLocked(ticket)) return;
                    _copyState = result.Copied ? CopyState.Copied : CopyState.Failed;
                    _copyFailureReason = result.Copied ? null : CopyReason(result.Reason);
                    retainCurrentState = true;
                    RecordWrite("copy", result.Copied ? "Copied" : _copyFailureReason!, ticket.Publication.Context);
                }
                if (result.Copied) NotifyTranslationCopied(ticket);
                PublishCopyPresentation(ticket);
            }
            catch (OperationCanceledException) when (ticket.Token.IsCancellationRequested) { }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (IsCopyCurrentLocked(ticket))
                    {
                        _copyState = CopyState.Failed;
                        _copyFailureReason = "ClipboardWriterFailed";
                        retainCurrentState = true;
                        RecordWrite("copy", error.GetType().Name, ticket.Publication.Context);
                    }
                }
                PublishCopyPresentation(ticket);
            }
            if (retainCurrentState)
            {
                try { await Task.Delay(Timeout.Infinite, ticket.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ticket.Token.IsCancellationRequested) { }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_copyTicket, ticket)) CancelCopyLocked();
            }
            await ticket.FinishAsync().ConfigureAwait(false);
        }
    }

    private async Task<TranslationClipboardCopyResult> AwaitCopyAsync(Task<TranslationClipboardCopyResult> task, CancellationToken token)
    {
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = ObserveAbandonedCopyAsync(task);
            throw;
        }
    }

    private async Task ObserveAbandonedCopyAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { RecordWrite("copy", "LateWriterFailure"); }
    }

    private void PublishCopyPresentation(CopyTicket ticket)
    {
        CandidateTranslationState state;
        lock (_gate)
        {
            if (!IsCopyCurrentLocked(ticket)) return;
            state = _coordinator.State;
        }
        if (!IsCopyCurrent(ticket)) return;
        NotifyCopyPresentationChanged();
        try { PreviewChanged?.Invoke(state); } catch (Exception) { }
    }

    private void NotifyCopyPresentationChanged()
    {
        var subscribers = CopyPresentationChanged;
        if (subscribers is null) return;
        foreach (Action subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(); } catch (Exception) { RecordWrite("copy", "PresentationSubscriberFailed"); }
        }
    }

    private void NotifyTranslationCopied(CopyTicket ticket)
    {
        if (!IsCopyCurrent(ticket)) return;
        var subscribers = TranslationCopied;
        if (subscribers is null) return;
        foreach (Action<string> subscriber in subscribers.GetInvocationList())
        {
            if (!IsCopyCurrent(ticket)) return;
            try { subscriber(ticket.Translation); }
            catch (Exception) { RecordWrite("copy", "CopiedSubscriberFailed", ticket.Publication.Context); }
        }
    }

    private string CopyHintLocked()
    {
        if (!Enabled) return "自动翻译复制已关闭；剪贴板内容保留。";
        var state = _coordinator.State;
        if (_copyTicket is { } ticket && IsCopyCurrentLocked(ticket))
        {
            return _copyState switch
            {
                CopyState.Copying => "正在复制英文，请等待已复制提示。",
                CopyState.Copied => "已复制英文；手动按 Esc 取消拼音，再 Ctrl+V 粘贴。",
                CopyState.Failed => $"复制失败（原因码：{_copyFailureReason ?? "ClipboardCopyFailed"}）；请继续输入后重试。",
                _ => "正在翻译当前首项，成功后将自动复制。"
            };
        }
        if (state.Error is not null) return "翻译失败，尚未复制英文。";
        if (state.Observation is null) return "等待微软拼音候选；翻译成功后将自动复制。";
        return "正在翻译当前首项，成功后将自动复制。";
    }

    private static string CopyReason(string reason) => string.IsNullOrWhiteSpace(reason) ? "ClipboardCopyFailed" :
        new string(reason.Take(80).Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_').ToArray());

    private sealed class CopyTicket
    {
        private readonly object _cancellationGate = new();
        private readonly CancellationTokenSource _cancellation;
        private Task? _cancellationTask;
        private bool _disposed;
        public CopyTicket(Publication publication, long generation, long epoch, long inputVersion, string translation, CancellationToken modeToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(modeToken);
            Publication = publication; Generation = generation; Epoch = epoch; InputVersion = inputVersion;
            Translation = translation; Token = _cancellation.Token;
        }
        public Publication Publication { get; }
        public long Generation { get; }
        public long Epoch { get; }
        public long InputVersion { get; }
        public string Translation { get; }
        public CancellationToken Token { get; }
        public Task CancelAsync()
        {
            lock (_cancellationGate)
            {
                if (_disposed) return Task.CompletedTask;
                return _cancellationTask ??= _cancellation.CancelAsync();
            }
        }
        public async Task FinishAsync()
        {
            try { await CancelAsync().ConfigureAwait(false); }
            catch (Exception) { }
            finally { lock (_cancellationGate) { if (!_disposed) { _disposed = true; _cancellation.Dispose(); } } }
        }
    }

    private void OnCoordinatorFaulted(Exception error)
    {
        if (_copyOnly) RecordWrite("translate", error.GetType().Name);
        else Report("拼音翻译失败：" + error.Message);
    }

    private void OnStateChanged(CandidateTranslationState state)
    {
        if (Volatile.Read(ref _disposed) != 0 || state.Generation != _coordinator.State.Generation) return;
        CopyTicket? copy = null;
        if (_copyOnly)
        {
            lock (_gate) copy = PrepareAutoCopyLocked();
            if (copy is not null) _ = Task.Run(() => RunAutoCopyAsync(copy));
            state = _coordinator.State;
            if (state.Observation is not null) NotifyCopyPresentationChanged();
        }
        try { PreviewChanged?.Invoke(state); } catch (Exception) { }
        if (state.Error is not null)
        {
            lock (_gate)
            {
                if (!Enabled || state.Generation != _coordinator.State.Generation) return;
                string error = _copyOnly ? CopyHintLocked() : state.Error == "输入适配器未接受当前候选。" ? CurrentAcceptanceNoticeLocked() ?? state.Error : state.Error;
                Report(error);
            }
        }
    }
    private void Report(string status) { if (Volatile.Read(ref _disposed) == 0) try { StatusChanged?.Invoke(status); } catch (Exception) { } }

    private static string ContextDiagnostic(EditableContextSnapshot live) =>
        $"valuePattern={live.HasValuePattern};textPattern={live.HasTextPattern};singleSelection={live.HasSingleSelection};" +
        $"collapsed={live.SelectionStartUtf16 == live.SelectionEndUtf16};canPreview={live.CanPreview};canReplace={live.CanReplace};" +
        $"readOnly={live.IsReadOnly};password={live.IsPassword};consistent={live.HasConsistentText};commit={live.CommitMethod};" +
        $"valueLength={live.Value.Length};documentLength={live.DocumentText.Length};plainLength={live.PlainText.Length}";

    private void Diagnose(string reason, string? details = null)
    {
        // Never enabled in the shipping runtime. Test logs contain states/counts only,
        // never candidate text, document text, translations, secrets or RuntimeIds.
        if (_testProcessId is null) return;
        string line;
        lock (_gate)
        {
            long sequence = ++_diagnosticSequence;
            line = $"{DateTimeOffset.UtcNow:O} process={Environment.ProcessId};seq={sequence};reason={reason};epoch={_epoch};input={_inputVersion};publication={_publicationVersion};" +
                $"enabled={Enabled};binding={_binding is not null};context={_context is not null};anchor={_anchor is not null};" +
                $"composing={_composing};compositionEpoch={_compositionEpoch};locationIntent={_locationIntentVersion};seen={_candidateSeen};pendingTab={_pendingTab is not null};" +
                $"absent={_candidateAbsent};writing={_writing};{details}{Environment.NewLine}";
        }
        _ = Task.Run(() =>
        {
            lock (_diagnosticGate)
            {
                try
                {
                    string directory = Path.Combine(Path.GetTempPath(), "PinyinTranslateDesktopHarness");
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, "ime-runtime-diagnostics.log"), line);
                }
                catch (Exception) { }
            }
        });
    }

    public void Dispose()
    {
        CancellationTokenSource? pollCancellation;
        lock (_gate)
        {
            if (_disposed != 0) return;
            Interlocked.Exchange(ref _disposed, 1);
            Interlocked.Exchange(ref _enabled, 0);
            _enableRequestVersion++; _epoch++;
            _compositionEpoch++; _candidateSeen = false; CancelPendingTabLocked("dispose");
            CancelCopyLocked();
            pollCancellation = _pollCancellation; _pollCancellation = null;
            _keyboard.Enabled = false;
        }
        _ = CancelPollingAsync(pollCancellation);
        _contexts.WriteProgress -= OnWriteProgress;
        _keyboard.Dispose(); _pointer.Dispose(); DetachBinding(); _coordinator.Dispose(); _contexts.Dispose();
        _clipboardWriter?.Dispose();
        _writeDiagnostics?.Dispose();
    }
}
