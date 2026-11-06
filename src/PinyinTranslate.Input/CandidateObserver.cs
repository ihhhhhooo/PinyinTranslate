using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace PinyinTranslate.Input;

public readonly record struct CandidateBounds(int X, int Y, int Width, int Height);
public sealed record CandidateObservation(long Version, nint ForegroundWindow, int FocusProcessId,
    string FocusRuntimeId, string Candidate, IReadOnlyList<string> VisiblePage, CandidateBounds Bounds);

/// <summary>
/// Experimental read-only IME observer. No COM objects cross the worker boundary.
/// UIA events invalidate immediately; observations are rate limited and inspect
/// only the Windows text-input host. This is deliberately
/// conservative and can miss other IME hosts or providers which omit events.
/// It must never be used as evidence that replacing the composition is safe.
/// </summary>
public sealed class CandidateObserver : ICandidateObserver
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maximumCount);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private readonly Process _targetProcess;
    private readonly int _allowedProcessId;
    private readonly string _allowedAutomationId;
    private readonly string? _allowedFocusRuntimeId;
    private readonly CandidateForegroundScope? _foregroundScope;
    private FocusIdentity? _lastFocusIdentity;
    private HashSet<int> _knownTextInputHostProcessIds = [];
    private string? _lastHostDiagnostic, _lastReadDiagnostic, _lastFocusDiagnostic;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _validationGate = new();
    private ValidationRequest? _validation;
    private bool _validationInProgress;
    private long _version, _modeEpoch;
    private int _enabled, _disposed, _running, _hostDiscoveryChanged;
    private CandidateObserverFailure? _failure;
    private sealed record ValidationRequest(CandidateObservation Snapshot, TaskCompletionSource<bool> Completion);
    private sealed record FocusIdentity(int ProcessId, string RuntimeId);
    public event Action<long>? Invalidated;
    /// <summary>
    /// A real focus identity change (or unreadable focus) must remain a cancellation boundary,
    /// even when a caller suppresses candidate invalidations caused by its own SetValue.
    /// Raised without observer locks; contains no COM objects or document content.
    /// </summary>
    public event Action? FocusInvalidated;
    public event Action<CandidateObservation>? Observed;
    public event Action<long>? CandidatesCleared;
    public event Action<string>? Status;
    /// <summary>For diagnostics only: limit reads to one explicitly approved target process.</summary>
    public CandidateObserver(Process targetProcess, string allowedAutomationId, string? allowedFocusRuntimeId = null,
        CandidateForegroundScope? foregroundScope = null)
    {
        ArgumentNullException.ThrowIfNull(targetProcess);
        int allowedProcessId = targetProcess.Id;
        if (allowedProcessId <= 0 || allowedProcessId == Environment.ProcessId) throw new ArgumentOutOfRangeException(nameof(allowedProcessId));
        if (string.IsNullOrWhiteSpace(allowedAutomationId) && string.IsNullOrWhiteSpace(allowedFocusRuntimeId))
            throw new ArgumentException("A focus runtime identity or AutomationId is required.");
        if (foregroundScope is not null && (!foregroundScope.IsValid || foregroundScope.ProcessId == Environment.ProcessId))
            throw new ArgumentException("A valid external foreground host is required.", nameof(foregroundScope));
        _foregroundScope = foregroundScope;
        if (targetProcess.HasExited) throw new InvalidOperationException("目标样例进程已退出。");
        _targetProcess = targetProcess;
        _allowedProcessId = allowedProcessId;
        _allowedAutomationId = allowedAutomationId;
        _allowedFocusRuntimeId = allowedFocusRuntimeId;
        _lastFocusIdentity = string.IsNullOrWhiteSpace(allowedFocusRuntimeId) ? null : new(allowedProcessId, allowedFocusRuntimeId);
        _worker = new Thread(Run) { IsBackground = true, Name = "IME UIA3 MTA" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }
    public Task<bool> Ready => _ready.Task;
    public bool IsRunning => Volatile.Read(ref _running) == 1 && Volatile.Read(ref _disposed) == 0;
    public CandidateObserverFailure? Failure => Volatile.Read(ref _failure);
    public void NotifyInputChanged() { if (Volatile.Read(ref _enabled) == 1) Invalidate(); }
    private bool IsCurrentGeneration(long version) => Volatile.Read(ref _running) == 1 && Volatile.Read(ref _enabled) == 1 &&
        Volatile.Read(ref _disposed) == 0 && Interlocked.Read(ref _version) == version;
    public bool IsCurrent(long version) => IsCurrentGeneration(version) && TargetAlive() &&
        IsAllowedForeground() && IsCurrentGeneration(version);
    public bool SetEnabled(bool enabled)
    {
        long version;
        lock (_validationGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || (enabled && Volatile.Read(ref _running) == 0)) return false;
            // Revoke old tickets before a new enabled epoch can become visible.
            version = Interlocked.Increment(ref _version);
            Interlocked.Increment(ref _modeEpoch);
            // Seed before the enabled epoch is observable. A provider's first
            // re-report of the already-bound editor is not a new focus transfer.
            Interlocked.Exchange(ref _lastFocusIdentity, enabled && !string.IsNullOrWhiteSpace(_allowedFocusRuntimeId)
                ? new FocusIdentity(_allowedProcessId, _allowedFocusRuntimeId) : null);
            Interlocked.Exchange(ref _lastFocusDiagnostic, null);
            Interlocked.Exchange(ref _enabled, enabled ? 1 : 0);
            if (!enabled) CancelValidationLocked();
        }
        NotifyInvalidated(version);
        return !enabled || Volatile.Read(ref _running) == 1;
    }
    private bool IsAllowedForeground()
    {
        nint foreground = GetForegroundWindow();
        return foreground != 0 && GetWindowThreadProcessId(foreground, out uint pid) != 0 &&
            (_foregroundScope is null ? pid == (uint)_allowedProcessId : _foregroundScope.Allows(foreground, pid));
    }
    private bool TargetAlive()
    {
        try { return !_targetProcess.HasExited; } catch (Exception) { return false; }
    }
    private void NotifyStatus(string message) { try { Status?.Invoke(message); } catch (Exception) { } }
    private void OnUiaEvent(long? subscriptionEpoch = null)
    {
        if (Volatile.Read(ref _enabled) == 1 && Volatile.Read(ref _disposed) == 0 &&
            (!subscriptionEpoch.HasValue || subscriptionEpoch.Value == Interlocked.Read(ref _modeEpoch))) Invalidate();
    }
    private static FocusIdentity? ReadFocusIdentity(AutomationElement? element, out int processId)
    {
        processId = 0;
        try
        {
            if (element is null || !element.Properties.ProcessId.IsSupported) return null;
            processId = element.Properties.ProcessId.ValueOrDefault;
            if (processId <= 0 || !element.Properties.RuntimeId.IsSupported) return null;
            string runtime = string.Join('.', element.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>());
            return runtime.Length > 0 ? new(processId, runtime) : null;
        }
        catch (Exception) { return null; }
    }
    private void OnFocusChanged(AutomationElement? reportedFocus, UIA3Automation automation)
    {
        long epoch = Interlocked.Read(ref _modeEpoch);
        if (Volatile.Read(ref _enabled) == 0 || Volatile.Read(ref _disposed) != 0) return;
        var reported = ReadFocusIdentity(reportedFocus, out int reportedPid);
        bool reportedByHost = Volatile.Read(ref _knownTextInputHostProcessIds).Contains(reportedPid);
        if (reportedPid == _allowedProcessId && reported is not null && !string.IsNullOrWhiteSpace(_allowedFocusRuntimeId) &&
            reported.RuntimeId != _allowedFocusRuntimeId)
        {
            if (Volatile.Read(ref _enabled) == 0 || Volatile.Read(ref _disposed) != 0 || epoch != Interlocked.Read(ref _modeEpoch)) return;
            Interlocked.Exchange(ref _lastFocusIdentity, reported);
            Diagnostic(ref _lastFocusDiagnostic, $"FOCUS_EVENT sourcePid={reportedPid};sourceRuntimeKnown=True;sourceIsTarget=True;sourceIsHost=False;decision=other-target-editor");
            // Preserve A -> B -> A as an invalidation even if B has already lost
            // focus by the time a subsequent current-focus query could complete.
            try { FocusInvalidated?.Invoke(); } catch (Exception) { }
            OnUiaEvent(epoch);
            return;
        }
        FocusIdentity? current = null;
        int currentPid = 0;
        bool actualTarget = false;
        try
        {
            // A global UIA focus event may describe virtual focus in the IME's
            // candidate window. Re-read the editor focus instead of adopting the
            // event sender as the keyboard insertion target.
            var focused = ReadFocusContext(automation);
            currentPid = focused?.FocusProcessId ?? 0;
            current = focused is null ? null : new(focused.FocusProcessId, focused.FocusRuntimeId);
            actualTarget = current is not null && IsAllowedForeground() && MatchesFocus(focused) && IsAllowedForeground();
        }
        catch (Exception) { /* Unknown actual focus remains a hard cancellation boundary. */ }
        if (Volatile.Read(ref _enabled) == 0 || Volatile.Read(ref _disposed) != 0 || epoch != Interlocked.Read(ref _modeEpoch)) return;
        var previous = Volatile.Read(ref _lastFocusIdentity);
        bool reportedOtherTarget = reportedPid == _allowedProcessId && reported is not null &&
            (!string.IsNullOrWhiteSpace(_allowedFocusRuntimeId) ? reported.RuntimeId != _allowedFocusRuntimeId : previous is not null && reported != previous);
        bool verifiedImeVirtualFocus = reportedByHost && actualTarget;
        bool reportedUnexpected = reported is null ? !verifiedImeVirtualFocus :
            reported.ProcessId != _allowedProcessId && !verifiedImeVirtualFocus;
        bool invalidateFocus = !actualTarget || reportedOtherTarget || reportedUnexpected ||
            (previous is not null && current != previous);
        if (current is not null)
            Interlocked.Exchange(ref _lastFocusIdentity, current);
        // Do not erase a known editor identity merely because a disappearing
        // provider could not be read; the unknown sample itself still invalidates.
        string decision = invalidateFocus ? "invalidate" : verifiedImeVirtualFocus ? "ignore-ime-virtual" : "same-editor";
        Diagnostic(ref _lastFocusDiagnostic, $"FOCUS_EVENT sourcePid={reportedPid};sourceRuntimeKnown={reported is not null};" +
            $"sourceIsTarget={reportedPid == _allowedProcessId};sourceIsHost={reportedByHost};currentPid={currentPid};" +
            $"currentRuntimeKnown={current is not null};actualTarget={actualTarget};previousKnown={previous is not null};decision={decision}");
        if (invalidateFocus)
        {
            try { FocusInvalidated?.Invoke(); } catch (Exception) { /* Subscribers cannot kill UIA callbacks. */ }
        }
        // Providers can re-report the same focused element after SetValue/Select. Those are
        // ordinary invalidations, not evidence of a distinct focus transaction.
        OnUiaEvent(epoch);
    }
    private void Diagnostic(ref string? previous, string code)
    {
        if (Interlocked.Exchange(ref previous, code) != code) NotifyStatus("IME_DIAG " + code);
    }
    private void Signal() { try { _signal.Set(); } catch (ObjectDisposedException) { } }
    private void Invalidate()
    {
        long version = Interlocked.Increment(ref _version);
        NotifyInvalidated(version);
    }
    private void NotifyInvalidated(long version)
    {
        try { Invalidated?.Invoke(version); } catch { /* Subscribers cannot kill UIA callbacks. */ }
        Signal();
    }

    /// <summary>Re-read on the UIA worker; a match is not proof of composition ownership or safe input.</summary>
    public async Task<bool> ValidateAsync(CandidateObservation snapshot, CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(snapshot.Version)) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_validationGate)
        {
            if (!IsCurrentGeneration(snapshot.Version) || _validation != null) return false;
            _validation = new(snapshot, completion);
        }
        Signal();
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            completion.TrySetResult(false);
            NotifyStatus("候选复核超时，当前结果已作废。");
            return false;
        }
        catch (OperationCanceledException) { completion.TrySetResult(false); return false; }
    }
    private void Run()
    {
        string stage = "uia-create";
        try
        {
            using var automation = new UIA3Automation
            {
                ConnectionTimeout = TimeSpan.FromMilliseconds(250),
                TransactionTimeout = TimeSpan.FromMilliseconds(250)
            };
            stage = "uia-desktop-subscribe";
            var root = automation.GetDesktop();
            // Window discovery must remain subscribed: the input host can appear only
            // after the first key. Discovery alone does not invalidate a candidate;
            // our own preview also creates a top-level window.
            using var discovery = root.RegisterStructureChangedEvent(TreeScope.Children, (_, _, _) =>
            {
                if (Volatile.Read(ref _enabled) == 0 || Volatile.Read(ref _disposed) != 0) return;
                Interlocked.Exchange(ref _hostDiscoveryChanged, 1);
                Signal();
            });
            stage = "uia-focus-subscribe";
            using var focus = automation.RegisterFocusChangedEvent(element => OnFocusChanged(element, automation));
            var subscriptions = new Dictionary<string, HostSubscription>();
            int previousHostCount = -1;
            long lastPublishedVersion = -1;
            long activeEpoch = -1;
            Interlocked.Exchange(ref _running, 1);
            stage = "uia-running";
            _ready.TrySetResult(true);
            NotifyStatus("本机候选诊断已启动；组合归属和输入安全仍需验证。");
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    _signal.WaitOne();
                    if (Volatile.Read(ref _disposed) != 0) break;
                    long currentEpoch = Interlocked.Read(ref _modeEpoch);
                    if (activeEpoch != currentEpoch)
                    {
                        DisposeHosts(subscriptions);
                        DrainValidations();
                        previousHostCount = -1;
                        lastPublishedVersion = -1;
                        activeEpoch = currentEpoch;
                    }
                    if (Volatile.Read(ref _enabled) == 0)
                    {
                        DisposeHosts(subscriptions);
                        DrainValidations();
                        previousHostCount = -1;
                        lastPublishedVersion = -1;
                        continue;
                    }
                    if (Interlocked.Exchange(ref _hostDiscoveryChanged, 0) != 0)
                    {
                        var previousHosts = subscriptions.Keys.Order().ToArray();
                        RefreshHosts(automation, automation.GetDesktop(), subscriptions, activeEpoch);
                        if (!previousHosts.SequenceEqual(subscriptions.Keys.Order())) Invalidate();
                    }
                    ProcessValidations(automation, subscriptions);
                    if (Interlocked.Read(ref _version) == lastPublishedVersion) continue;
                    // A validation wake-up is not an input event and does not restart the quiet period.
                    long quietVersion = Interlocked.Read(ref _version);
                    long lastChange = Stopwatch.GetTimestamp();
                    long waitingSince = lastChange;
                    bool waitingReported = false;
                    while (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _enabled) == 1 &&
                           Interlocked.Read(ref _modeEpoch) == activeEpoch)
                    {
                        var remaining = TimeSpan.FromMilliseconds(100) - Stopwatch.GetElapsedTime(lastChange);
                        if (remaining <= TimeSpan.Zero)
                        {
                            long finalVersion = Interlocked.Read(ref _version);
                            if (finalVersion == quietVersion) break;
                            quietVersion = finalVersion;
                            lastChange = Stopwatch.GetTimestamp();
                            continue;
                        }
                        if (_signal.WaitOne(remaining)) ProcessValidations(automation, subscriptions);
                        long currentVersion = Interlocked.Read(ref _version);
                        if (currentVersion != quietVersion)
                        {
                            quietVersion = currentVersion;
                            lastChange = Stopwatch.GetTimestamp();
                        }
                        if (!waitingReported && Stopwatch.GetElapsedTime(waitingSince) >= TimeSpan.FromSeconds(3))
                        {
                            NotifyStatus("候选事件持续变化，正在等待稳定；不会强制发布。");
                            waitingReported = true;
                        }
                    }
                    if (Volatile.Read(ref _disposed) != 0) break;
                    if (Interlocked.Read(ref _modeEpoch) != activeEpoch)
                    {
                        Signal();
                        continue;
                    }
                    if (Volatile.Read(ref _enabled) == 0)
                    {
                        DisposeHosts(subscriptions);
                        DrainValidations();
                        previousHostCount = -1;
                        lastPublishedVersion = -1;
                        continue;
                    }
                    try
                    {
                        RefreshHosts(automation, automation.GetDesktop(), subscriptions, activeEpoch);
                        if (subscriptions.Count != previousHostCount)
                        {
                            previousHostCount = subscriptions.Count;
                            NotifyStatus($"输入法候选宿主数：{previousHostCount}；此功能仍在验证中。");
                        }
                        long version = quietVersion;
                        if (!IsCurrent(version)) continue;
                        var snapshot = Read(automation, subscriptions.Values.Select(s => s.Host), version);
                        if (snapshot != null && version != lastPublishedVersion && IsCurrent(version))
                        {
                            lastPublishedVersion = version;
                            try { Observed?.Invoke(snapshot); } catch (Exception) { }
                        }
                        else if (snapshot is null && IsCurrent(version) &&
                                 ConfirmNoVisibleCandidates(automation, subscriptions.Values.Select(s => s.Host), version))
                        {
                            lastPublishedVersion = version;
                            try { CandidatesCleared?.Invoke(version); } catch (Exception) { }
                        }
                    }
                    catch (Exception error)
                    {
                        // Metadata only: never include provider messages or candidate/document text.
                        Diagnostic(ref _lastReadDiagnostic, "PROVIDER_READ_EXCEPTION=" + error.GetType().Name);
                    }
                }
            }
            finally { DisposeHosts(subscriptions); }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _failure, CandidateObserverFailure.FromException(e, stage));
            NotifyStatus("候选观察器不可用：" + Failure!.ToUserMessage());
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            lock (_validationGate)
            {
                Interlocked.Exchange(ref _enabled, 0);
                CancelValidationLocked();
            }
            _ready.TrySetResult(false);
            try { _signal.Dispose(); } catch (Exception) { }
        }
    }

    private static bool Matches(CandidateObservation expected, CandidateObservation? live) =>
        live != null && expected.Version == live.Version && expected.ForegroundWindow == live.ForegroundWindow &&
        expected.FocusProcessId == live.FocusProcessId && expected.FocusRuntimeId == live.FocusRuntimeId &&
        expected.Candidate == live.Candidate && expected.Bounds == live.Bounds && expected.VisiblePage.SequenceEqual(live.VisiblePage);

    private void ProcessValidations(UIA3Automation automation, Dictionary<string, HostSubscription> subscriptions)
    {
        ValidationRequest request;
        lock (_validationGate)
        {
            if (_validation is not { } pending || _validationInProgress) return;
            request = pending;
            _validationInProgress = true;
        }
        try
        {
            bool valid = !request.Completion.Task.IsCompleted && IsCurrent(request.Snapshot.Version) &&
                Matches(request.Snapshot, Read(automation, subscriptions.Values.Select(s => s.Host), request.Snapshot.Version)) &&
                IsCurrent(request.Snapshot.Version);
            request.Completion.TrySetResult(valid);
        }
        catch (Exception) { request.Completion.TrySetResult(false); }
        finally
        {
            lock (_validationGate)
            {
                if (ReferenceEquals(_validation, request)) _validation = null;
                _validationInProgress = false;
            }
        }
    }

    private void DrainValidations()
    {
        lock (_validationGate) CancelValidationLocked();
    }

    private void CancelValidationLocked()
    {
        _validation?.Completion.TrySetResult(false);
        if (!_validationInProgress) _validation = null;
    }

    private static void DisposeHosts(Dictionary<string, HostSubscription> subscriptions)
    {
        foreach (var subscription in subscriptions.Values)
            try { subscription.Dispose(); } catch (Exception) { }
        subscriptions.Clear();
    }

    private sealed record HostSubscription(AutomationElement Host, IDisposable Structure, IDisposable Property) : IDisposable
    {
        public void Dispose() { try { Property.Dispose(); } finally { Structure.Dispose(); } }
    }
    private void RefreshHosts(UIA3Automation automation, AutomationElement root, Dictionary<string, HostSubscription> subscriptions, long epoch)
    {
        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("TextInputHost"))
            using (process) processIds.Add(process.Id);
        Volatile.Write(ref _knownTextInputHostProcessIds, processIds);
        var found = new Dictionary<string, AutomationElement>();
        AutomationElement[] frames = [];
        int uiaFrameFailures = 0, nativeFrameFailures = 0;
        int coreWindowCount = 0, nativeCoreWindowCount = 0, nativeMappedFrames = 0;
        int ReadDirectHosts(AutomationElement frame)
        {
            // Only this observed direct relationship is inspected. No element names,
            // application document content, or arbitrary desktop descendants are read.
            var children = frame.FindAllChildren(cf => cf.ByClassName("Windows.UI.Core.CoreWindow"));
            foreach (var host in children)
            {
                if (!host.Properties.ProcessId.IsSupported || !processIds.Contains(host.Properties.ProcessId.ValueOrDefault) ||
                    !host.Properties.RuntimeId.IsSupported) continue;
                string runtime = string.Join('.', host.Properties.RuntimeId.ValueOrDefault ?? Array.Empty<int>());
                if (runtime.Length > 0) found.TryAdd(runtime, host);
            }
            return children.Length;
        }
        try { frames = root.FindAllChildren(cf => cf.ByClassName("ApplicationFrameWindow")); }
        catch (Exception) { uiaFrameFailures++; }
        foreach (var frame in frames)
        {
            try { coreWindowCount += ReadDirectHosts(frame); }
            catch (Exception) { uiaFrameFailures++; }
        }
        // A WPF-hosted client can miss the frame in the desktop UIA child view even
        // while a separate diagnostic process sees it. Anchor the same narrow query
        // to public HWNDs rather than relying solely on that desktop enumeration.
        var nativeFrames = FindNativeFrames(out bool nativeEnumerationSucceeded, out int nativeClassMatches);
        foreach (nint frameWindow in nativeFrames)
        {
            if (Volatile.Read(ref _enabled) == 0 || Interlocked.Read(ref _modeEpoch) != epoch) return;
            try
            {
                var frame = automation.FromHandle(frameWindow);
                nativeMappedFrames++;
                nativeCoreWindowCount += ReadDirectHosts(frame);
            }
            catch (Exception) { nativeFrameFailures++; }
        }
        Diagnostic(ref _lastHostDiagnostic, $"HOST_SCAN textInputProcesses={processIds.Count};frames={frames.Length};coreWindows={coreWindowCount};" +
            $"nativeEnumOk={nativeEnumerationSucceeded};nativeClassMatches={nativeClassMatches};nativeFrames={nativeFrames.Count};nativeMapped={nativeMappedFrames};nativeCoreWindows={nativeCoreWindowCount};" +
            $"uiaFailures={uiaFrameFailures};nativeFailures={nativeFrameFailures};hosts={found.Count}");
        foreach (var key in subscriptions.Keys.Except(found.Keys).ToArray())
        {
            try { subscriptions[key].Dispose(); } catch (Exception) { }
            subscriptions.Remove(key);
        }
        foreach (var (key, host) in found)
        {
            if (Volatile.Read(ref _enabled) == 0 || Interlocked.Read(ref _modeEpoch) != epoch) return;
            if (subscriptions.ContainsKey(key)) continue;
            var structure = host.RegisterStructureChangedEvent(TreeScope.Subtree, (_, _, _) => OnUiaEvent(epoch));
            try
            {
                var property = host.RegisterPropertyChangedEvent(TreeScope.Subtree, (_, _, _) => OnUiaEvent(epoch),
                    automation.PropertyLibrary.Element.Name, automation.PropertyLibrary.Element.IsOffscreen,
                    automation.PropertyLibrary.Element.BoundingRectangle);
                subscriptions.Add(key, new(host, structure, property));
            }
            catch { structure.Dispose(); throw; }
        }
    }
    private static IReadOnlyList<nint> FindNativeFrames(out bool succeeded, out int classMatchCount)
    {
        var frames = new List<nint>();
        var className = new StringBuilder(256);
        EnumWindowCallback callback = (window, _) =>
        {
            try
            {
                className.Clear();
                if (GetClassName(window, className, className.Capacity) > 0 && className.ToString() == "ApplicationFrameWindow")
                    frames.Add(window);
            }
            catch (Exception) { /* A disappearing window must not escape a native callback. */ }
            return true;
        };
        // Hidden top-level frames are intentionally retained so a host can be
        // subscribed before the user's first composition becomes visible.
        succeeded = EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        var uniqueFrames = new HashSet<nint>(frames);
        var classMatches = new HashSet<nint>();
        // EnumWindows omits some shell/UWP frames on supported Windows versions.
        // FindWindowEx performs the same exact-class query without a desktop UIA
        // root walk. Never inspect arbitrary window names or infer a private HWND.
        foreach (nint parent in new nint[] { 0, GetDesktopWindow() })
        {
            nint after = 0;
            var visited = new HashSet<nint>();
            for (int attempt = 0; attempt < 256; attempt++)
            {
                nint window = FindWindowEx(parent, after, "ApplicationFrameWindow", null);
                if (window == 0 || !visited.Add(window)) break;
                className.Clear();
                if (GetClassName(window, className, className.Capacity) > 0 && className.ToString() == "ApplicationFrameWindow")
                {
                    classMatches.Add(window);
                    if (uniqueFrames.Add(window)) frames.Add(window);
                }
                after = window;
            }
        }
        classMatchCount = classMatches.Count;
        return frames;
    }
    private CandidateObservation? Read(UIA3Automation automation, IEnumerable<AutomationElement> hosts, long version)
    {
        CandidateObservation? Reject(string code) { Diagnostic(ref _lastReadDiagnostic, code); return null; }
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || !IsCurrent(version)) return Reject("FOREGROUND_OR_VERSION_REJECTED");
        var focused = ReadFocusContext(automation);
        if (!MatchesFocus(focused)) return Reject("FOCUS_IDENTITY_OR_PASSWORD_REJECTED");
        int pid = focused!.FocusProcessId;
        if (pid != _allowedProcessId || !TargetAlive()) return null;
        var runtime = focused.FocusRuntimeId;
        if (runtime.Length == 0) return null;
        // ReadFocusContext has already verified the raw-view ancestry against
        // this foreground root without requiring a text/edit control role.
        if (focused.ForegroundWindow != foreground) return Reject("FOCUS_FOREGROUND_ROOT_MISMATCH");
        var matches = new List<AutomationElement>();
        foreach (var host in hosts)
        {
            foreach (var element in host.FindAllDescendants(cf => cf.ByAutomationId("IME_Candidate_Window")))
            {
                if (!element.Properties.IsOffscreen.IsSupported) return null;
                if (!element.Properties.IsOffscreen.ValueOrDefault) matches.Add(element);
            }
        }
        if (matches.Count != 1) return Reject($"VISIBLE_MENU_COUNT={matches.Count}");
        var menu = matches[0];
        var menuBounds = VisibleBounds(menu);
        if (menuBounds is null) return Reject("MENU_BOUNDS_UNAVAILABLE");
        var panel = menu.FindFirstDescendant(cf => cf.ByAutomationId("TEMPLATE_PART_CandidatePanel"));
        if (panel == null || !panel.Properties.ControlType.IsSupported || panel.Properties.ControlType.ValueOrDefault != ControlType.List) return Reject("CANDIDATE_PANEL_UNAVAILABLE");
        var panelBounds = VisibleBounds(panel);
        if (panelBounds is null || !Intersects(menuBounds.Value, panelBounds.Value)) return Reject("CANDIDATE_PANEL_BOUNDS_REJECTED");
        var names = new List<string>();
        CandidateBounds? previous = null;
        long commonTop = long.MinValue, commonBottom = long.MaxValue;
        foreach (var item in panel.FindAllChildren())
        {
            if (!item.Properties.ControlType.IsSupported) return null;
            if (item.Properties.ControlType.ValueOrDefault != ControlType.ListItem) continue;
            if (!item.Properties.IsOffscreen.IsSupported) return null;
            if (item.Properties.IsOffscreen.ValueOrDefault) continue;
            var bounds = VisibleBounds(item);
            if (bounds is null || !Contains(panelBounds.Value, bounds.Value) || !Contains(menuBounds.Value, bounds.Value) ||
                !item.Properties.Name.IsSupported) return Reject("CANDIDATE_ITEM_BOUNDS_OR_NAME_REJECTED");
            string name = item.Properties.Name.ValueOrDefault ?? "";
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || names.Count >= 20) return null;
            // Only a single unambiguous left-to-right row is currently supported.
            commonTop = Math.Max(commonTop, bounds.Value.Y);
            commonBottom = Math.Min(commonBottom, (long)bounds.Value.Y + bounds.Value.Height);
            if (commonTop >= commonBottom || (previous is { } prior && bounds.Value.X < (long)prior.X + prior.Width)) return Reject("CANDIDATE_ROW_ORDER_REJECTED");
            names.Add(name);
            previous = bounds;
        }
        if (names.Count == 0 || !IsCurrent(version) || GetForegroundWindow() != foreground) return Reject("EMPTY_PAGE_OR_CHANGED_READ_BOUNDARY");
        var focusedAgain = ReadFocusContext(automation);
        if (!MatchesFocus(focusedAgain) ||
            focusedAgain!.FocusProcessId != pid || focusedAgain.FocusRuntimeId != runtime ||
            focusedAgain.ForegroundWindow != foreground) return null;
        if (!TargetAlive() || !IsCurrent(version) || GetForegroundWindow() != foreground) return null;
        Diagnostic(ref _lastReadDiagnostic, $"CANDIDATE_READ_OK visibleItems={names.Count}");
        return new(version, foreground, pid, runtime, names[0], Array.AsReadOnly(names.ToArray()), menuBounds.Value);
    }

    private static EditableContextSnapshot? ReadFocusContext(UIA3Automation automation) =>
        PreviewFocusReader.Read(new UiaPreviewFocusEnvironment(automation));
    private bool MatchesFocus(EditableContextSnapshot? focused) => focused is { CanPreview: true, IsPassword: false } &&
        focused.FocusProcessId == _allowedProcessId &&
        (string.IsNullOrEmpty(_allowedAutomationId) || focused.AutomationId == _allowedAutomationId) &&
        (_allowedFocusRuntimeId is null || focused.FocusRuntimeId == _allowedFocusRuntimeId);

    private bool ConfirmNoVisibleCandidates(UIA3Automation automation, IEnumerable<AutomationElement> hosts, long version)
    {
        if (!IsCurrent(version) || !MatchesFocus(ReadFocusContext(automation))) return false;
        bool inspectedHost = false;
        foreach (var host in hosts)
        {
            inspectedHost = true;
            foreach (var menu in host.FindAllDescendants(cf => cf.ByAutomationId("IME_Candidate_Window")))
                if (!menu.Properties.IsOffscreen.IsSupported || !menu.Properties.IsOffscreen.ValueOrDefault) return false;
        }
        return inspectedHost && IsCurrent(version) && MatchesFocus(ReadFocusContext(automation));
    }

    private static CandidateBounds? VisibleBounds(AutomationElement element)
    {
        if (!element.Properties.IsOffscreen.IsSupported || element.Properties.IsOffscreen.ValueOrDefault ||
            !element.Properties.BoundingRectangle.IsSupported) return null;
        var bounds = element.Properties.BoundingRectangle.ValueOrDefault;
        return bounds.Width > 0 && bounds.Height > 0 ? new(bounds.X, bounds.Y, bounds.Width, bounds.Height) : null;
    }

    private static bool Intersects(CandidateBounds a, CandidateBounds b) =>
        (long)a.X < (long)b.X + b.Width && (long)b.X < (long)a.X + a.Width && VerticalOverlap(a, b);

    private static bool Contains(CandidateBounds outer, CandidateBounds inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;

    private static bool VerticalOverlap(CandidateBounds a, CandidateBounds b) =>
        (long)a.Y < (long)b.Y + b.Height && (long)b.Y < (long)a.Y + a.Height;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_validationGate)
        {
            Interlocked.Exchange(ref _enabled, 0);
            CancelValidationLocked();
        }
        Invalidate();
        if (Thread.CurrentThread != _worker) _worker.Join(TimeSpan.FromSeconds(2));
        // The worker owns the signal and disposes it after a stalled provider returns.
    }
}
