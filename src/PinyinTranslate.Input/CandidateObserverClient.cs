using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;

namespace PinyinTranslate.Input;

/// <summary>
/// Process-isolated candidate observer. Hook-facing methods touch local state and
/// nonblocking queues only. Every remote event is fenced by session/epoch/input sequence.
/// No credentials or document text are sent to the bridge.
/// </summary>
public sealed class CandidateObserverClient : ICandidateObserver
{
    private readonly object _gate = new();
    private readonly Process _target;
    private readonly string _automationId, _runtimeId, _sessionId = Guid.NewGuid().ToString("N");
    private readonly int _targetPid;
    private readonly long _targetStartTicks;
    private readonly string? _bridgePath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<CandidateBridgeMessage> _commands = Channel.CreateBounded<CandidateBridgeMessage>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly Channel<Action> _notifications = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Dictionary<long, PendingValidation> _validations = new();
    private long _epoch, _inputSequence, _version, _requestId, _sourceFloor = long.MinValue, _lastPublishedSource = long.MinValue;
    private bool _enabled, _running, _hasEvidence;
    private int _disposed, _failed;
    private string _startupStage = "created";
    private string? _failureDiagnostic;
    private CandidateObserverFailure? _failure;
    private CandidateObservation? _current;
    private CandidateBridgeObservation? _remoteCurrent;
    private sealed record PendingValidation(long Epoch, long InputSequence, long SourceVersion, TaskCompletionSource<bool> Completion);

    public CandidateObserverClient(Process targetProcess, string allowedAutomationId, string? allowedFocusRuntimeId = null, string? bridgePath = null)
    {
        ArgumentNullException.ThrowIfNull(targetProcess);
        _targetPid = targetProcess.Id;
        if (_targetPid <= 0 || _targetPid == Environment.ProcessId) throw new ArgumentOutOfRangeException(nameof(targetProcess));
        if (string.IsNullOrWhiteSpace(allowedAutomationId) && string.IsNullOrWhiteSpace(allowedFocusRuntimeId))
            throw new ArgumentException("A focus runtime identity or AutomationId is required.");
        _target = Process.GetProcessById(_targetPid);
        try
        {
            // Process.Handle requests broad access on Windows. Liveness and
            // creation-time queries need only the runtime's limited query rights.
            if (_target.HasExited) throw new InvalidOperationException("Target process has exited.");
            _targetStartTicks = _target.StartTime.ToUniversalTime().Ticks;
        }
        catch { _target.Dispose(); throw; }
        _automationId = allowedAutomationId ?? "";
        _runtimeId = allowedFocusRuntimeId ?? "";
        _bridgePath = bridgePath;
        _ = Task.Run(DrainNotificationsAsync);
        _ = Task.Run(RunAsync);
    }

    public Task<bool> Ready => _ready.Task;
    public bool IsRunning { get { lock (_gate) return _disposed == 0 && _failed == 0 && _running; } }
    public CandidateObserverFailure? Failure => Volatile.Read(ref _failure);
    public string StartupStage => Volatile.Read(ref _startupStage);
    public string? FailureDiagnostic => Volatile.Read(ref _failureDiagnostic);
    public event Action<long>? Invalidated;
    public event Action? FocusInvalidated;
    public event Action<CandidateObservation>? Observed;
    public event Action<long>? CandidatesCleared;
    public event Action<string>? Status;

    public bool IsCurrent(long version)
    {
        lock (_gate) return _disposed == 0 && _running && _enabled && _hasEvidence && version == _version;
    }

    public bool SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed != 0 || (enabled && !_running)) return false;
            _epoch++; _inputSequence++; _enabled = enabled;
            InvalidateLocked();
            return QueueLocked(new() { Kind = "enabled", Enabled = enabled });
        }
    }

    public void NotifyInputChanged()
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_enabled || !_running) return;
            _inputSequence++;
            InvalidateLocked();
            _ = QueueLocked(new() { Kind = "input" });
        }
    }

    public async Task<bool> ValidateAsync(CandidateObservation snapshot, CancellationToken cancellationToken = default)
    {
        PendingValidation pending;
        long request;
        lock (_gate)
        {
            if (!IsCurrent(snapshot.Version) || _current is null || _remoteCurrent is null || !SameObservation(snapshot, _current) || _validations.Count >= 32) return false;
            request = ++_requestId;
            pending = new(_epoch, _inputSequence, _remoteCurrent.SourceVersion, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _validations.Add(request, pending);
            if (!QueueLocked(new() { Kind = "validate", RequestId = request, SourceVersion = pending.SourceVersion, Observation = _remoteCurrent })) return false;
        }
        try { return await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false) && IsCurrent(snapshot.Version); }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) { return false; }
        finally { lock (_gate) { if (_validations.Remove(request, out var removed)) removed.Completion.TrySetResult(false); } }
    }

    private bool QueueLocked(CandidateBridgeMessage message)
    {
        if (_commands.Writer.TryWrite(message with { SessionId = _sessionId, Epoch = _epoch, InputSequence = _inputSequence })) return true;
        // Never drop an input barrier and continue trusting future remote messages.
        _running = false; _enabled = false;
        InvalidateLocked();
        _ = Task.Run(() => Fail(new(CandidateObserverFailureCode.TransportUnavailable, "command-queue")));
        return false;
    }

    private void InvalidateLocked()
    {
        _hasEvidence = false; _current = null; _remoteCurrent = null;
        long version = ++_version;
        foreach (var pending in _validations.Values) pending.Completion.TrySetResult(false);
        _validations.Clear();
        long epoch = _epoch, inputSequence = _inputSequence;
        _notifications.Writer.TryWrite(() => { if (IsNoticeCurrent(epoch, inputSequence, version)) Invalidated?.Invoke(version); });
    }

    private bool IsNoticeCurrent(long epoch, long inputSequence, long? version = null)
    {
        lock (_gate) return _disposed == 0 && _epoch == epoch && _inputSequence == inputSequence && (!version.HasValue || _version == version.Value);
    }

    private void QueueFocusLocked()
    {
        // A focus event is commonly followed by an ordinary invalidation in
        // the same input sequence. That version advance must not erase focus loss.
        long epoch = _epoch, inputSequence = _inputSequence;
        _notifications.Writer.TryWrite(() => { if (IsNoticeCurrent(epoch, inputSequence)) FocusInvalidated?.Invoke(); });
    }

    private void QueueStatusLocked(string status)
    {
        long epoch = _epoch, inputSequence = _inputSequence;
        _notifications.Writer.TryWrite(() => { if (IsNoticeCurrent(epoch, inputSequence)) Status?.Invoke(status); });
    }

    private async Task RunAsync()
    {
        Process? bridge = null;
        try
        {
            StartupStageChanged("pipe-create");
            string pipeName = $"PinyinTranslate.ImeBridge.{Environment.ProcessId}.{Guid.NewGuid():N}";
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            StartupStageChanged("resolve-package");
            string executable = ResolveBridgePath();
            var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            // The helper needs Windows/runtime paths, never application API secrets.
            start.Environment.Clear();
            foreach (string name in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "DOTNET_ROOT", "DOTNET_ROOT(X86)" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            foreach (string value in new[] { "--pipe", pipeName, "--parent", Environment.ProcessId.ToString(), "--session", _sessionId }) start.ArgumentList.Add(value);
            StartupStageChanged("process-start");
            bridge = Process.Start(start) ?? throw new InvalidOperationException("Could not start IME bridge.");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(6));
            StartupStageChanged("pipe-connect");
            Task exited = bridge.WaitForExitAsync(_lifetime.Token);
            Task connected = pipe.WaitForConnectionAsync(startup.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
            {
                Fail(new(CandidateObserverFailureCode.BridgeExited, StartupStage), bridge);
                return;
            }
            await connected.ConfigureAwait(false);
            if (!CandidateBridgeProtocol.IsPipeClient(pipe, bridge.Id)) throw new InvalidDataException("Bridge pipe client PID mismatch.");
            using var parent = Process.GetCurrentProcess();
            StartupStageChanged("hello-send");
            await CandidateBridgeProtocol.WriteAsync(pipe, new()
            {
                Kind = "hello", SessionId = _sessionId, ParentProcessId = Environment.ProcessId,
                ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks,
                TargetProcessId = _targetPid, TargetStartTicks = _targetStartTicks,
                AutomationId = _automationId, FocusRuntimeId = _runtimeId
            }, startup.Token).ConfigureAwait(false);
            StartupStageChanged("ready-await");
            var ready = await CandidateBridgeProtocol.ReadAsync(pipe, startup.Token).ConfigureAwait(false);
            if (ready.Kind != "ready" || ready.SessionId != _sessionId || ready.WorkerHealthVersion != 2)
                throw new InvalidDataException("Invalid bridge handshake or incompatible worker.");
            if (!ready.Success)
            {
                Fail(ready.Failure ?? new(CandidateObserverFailureCode.ObserverInitializationFailed, "uia-startup"), bridge);
                return;
            }
            lock (_gate) { if (_disposed != 0) return; _running = true; }
            StartupStageChanged("running");
            _ready.TrySetResult(true);
            Task reader = ReadEventsAsync(pipe, _lifetime.Token);
            Task writer = WriteCommandsAsync(pipe, _lifetime.Token);
            Task targetExited = _target.WaitForExitAsync(_lifetime.Token);
            Task completed = await Task.WhenAny(reader, writer, exited, targetExited).ConfigureAwait(false);
            if (completed == exited || completed == targetExited)
            {
                lock (_gate)
                {
                    // Revoke input immediately while allowing already-flushed
                    // terminal diagnostics to arrive before process-exit fallback.
                    _enabled = false;
                    InvalidateLocked();
                    QueueFocusLocked();
                }
                try { await reader.WaitAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false); }
                catch (Exception) { /* EOF/timeout is the normal exit fallback. */ }
                if (!_lifetime.IsCancellationRequested)
                    Fail(new(completed == targetExited ? CandidateObserverFailureCode.TargetExited : CandidateObserverFailureCode.BridgeExited,
                        StartupStage), bridge);
                return;
            }
            await completed.ConfigureAwait(false);
            if (!_lifetime.IsCancellationRequested)
                Fail(new(completed == targetExited ? CandidateObserverFailureCode.TargetExited :
                    completed == exited ? CandidateObserverFailureCode.BridgeExited : CandidateObserverFailureCode.TransportUnavailable, StartupStage), bridge);
        }
        catch (Exception error) when (error is not StackOverflowException)
        {
            if (Volatile.Read(ref _disposed) == 0) Fail(CandidateObserverFailure.FromException(error, StartupStage), bridge);
        }
        finally
        {
            _ready.TrySetResult(false);
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _commands.Writer.TryComplete();
            if (bridge is not null)
            {
                try { if (!bridge.HasExited) bridge.Kill(); } catch (Exception) { }
                bridge.Dispose();
            }
            _target.Dispose();
        }
    }

    private async Task WriteCommandsAsync(Stream pipe, CancellationToken cancellation)
    {
        await foreach (var command in _commands.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
            await CandidateBridgeProtocol.WriteAsync(pipe, command, cancellation).ConfigureAwait(false);
    }

    private async Task ReadEventsAsync(Stream pipe, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var message = await CandidateBridgeProtocol.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed != 0 || !_running || message.SessionId != _sessionId) continue;
                if (message.Kind == "failed")
                {
                    Fail(message.Failure ?? new(CandidateObserverFailureCode.Unexpected, "running"));
                    return;
                }
                // Worker health belongs to the authenticated process/session,
                // independent of a rapidly changing candidate input sequence.
                if (message.Kind == "heartbeat")
                {
                    if (!message.Success)
                    {
                        Fail(message.Failure ?? new(CandidateObserverFailureCode.ObserverInitializationFailed, "uia-running"));
                        return;
                    }
                    continue;
                }
                if (message.Epoch != _epoch || message.InputSequence != _inputSequence) continue;
                switch (message.Kind)
                {
                    case "status":
                        string status = (message.Status ?? "")[..Math.Min(message.Status?.Length ?? 0, 2048)];
                        QueueStatusLocked(status);
                        break;
                    case "focus":
                        InvalidateLocked();
                        QueueFocusLocked();
                        break;
                    case "invalidated":
                        // A delayed invalidation for the generation whose stable
                        // snapshot was already published cannot revoke that snapshot.
                        if (message.SourceVersion < _sourceFloor || message.SourceVersion <= _lastPublishedSource) break;
                        _sourceFloor = message.SourceVersion;
                        InvalidateLocked();
                        break;
                    case "observed":
                    case "cleared":
                        if (!_enabled || message.SourceVersion < _sourceFloor || message.SourceVersion <= _lastPublishedSource) break;
                        if (message.Kind == "observed" && !IsValidObservation(message.Observation, message.SourceVersion)) throw new InvalidDataException("Invalid candidate metadata.");
                        InvalidateLocked();
                        _lastPublishedSource = message.SourceVersion; _sourceFloor = message.SourceVersion;
                        _hasEvidence = true;
                        long version = _version;
                        if (message.Kind == "cleared") _notifications.Writer.TryWrite(() => { if (IsCurrent(version)) CandidatesCleared?.Invoke(version); });
                        else
                        {
                            _remoteCurrent = message.Observation;
                            var observed = message.Observation!.ToObservation(version);
                            _current = observed;
                            _notifications.Writer.TryWrite(() => { if (IsCurrent(version)) Observed?.Invoke(observed); });
                        }
                        break;
                    case "validated":
                        if (_validations.TryGetValue(message.RequestId, out var pending) && pending.Epoch == message.Epoch &&
                            pending.InputSequence == message.InputSequence && pending.SourceVersion == message.SourceVersion)
                        { _validations.Remove(message.RequestId); pending.Completion.TrySetResult(message.Success); }
                        break;
                    default: throw new InvalidDataException("Unknown bridge event.");
                }
            }
        }
    }

    private bool IsValidObservation(CandidateBridgeObservation? value, long sourceVersion) => value is not null &&
        value.SourceVersion == sourceVersion && value.ForegroundWindow != 0 && value.FocusProcessId == _targetPid &&
        !string.IsNullOrWhiteSpace(value.FocusRuntimeId) && (_runtimeId.Length == 0 || value.FocusRuntimeId == _runtimeId) &&
        value.Candidate.Length is > 0 and <= 256 && value.VisiblePage is { Length: > 0 and <= 64 } &&
        value.VisiblePage.All(candidate => candidate is { Length: > 0 and <= 256 }) && value.VisiblePage[0] == value.Candidate &&
        value.Bounds.Width > 0 && value.Bounds.Height > 0;

    private static bool SameObservation(CandidateObservation left, CandidateObservation right) => left.Version == right.Version &&
        left.ForegroundWindow == right.ForegroundWindow && left.FocusProcessId == right.FocusProcessId && left.FocusRuntimeId == right.FocusRuntimeId &&
        left.Candidate == right.Candidate && left.Bounds == right.Bounds && left.VisiblePage.SequenceEqual(right.VisiblePage);

    private string ResolveBridgePath()
    {
        if (_bridgePath is not null) return IsCompleteBridgePackage(_bridgePath) ? Path.GetFullPath(_bridgePath) : throw new FileNotFoundException("IME bridge package is incomplete.");
        foreach (string candidate in new[] { Path.Combine(AppContext.BaseDirectory, "PinyinTranslate.ImeBridge.exe"), Path.Combine(AppContext.BaseDirectory, "ImeBridge", "PinyinTranslate.ImeBridge.exe") })
            if (IsCompleteBridgePackage(candidate)) return candidate;
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int level = 0; directory is not null && level < 12; level++, directory = directory.Parent)
            foreach (string configuration in new[] { "Release", "Debug" })
            {
                string candidate = Path.Combine(directory.FullName, "src", "PinyinTranslate.ImeBridge", "bin", configuration, "net10.0-windows", "PinyinTranslate.ImeBridge.exe");
                if (IsCompleteBridgePackage(candidate)) return candidate;
            }
        throw new FileNotFoundException("IME bridge executable was not found.");
    }

    private static bool IsCompleteBridgePackage(string executable) => File.Exists(executable) &&
        File.Exists(Path.ChangeExtension(executable, ".dll")) && File.Exists(Path.ChangeExtension(executable, ".runtimeconfig.json"));

    private void StartupStageChanged(string stage)
    {
        Volatile.Write(ref _startupStage, stage);
        // Local startup diagnostics carry no candidate/session data and survive
        // immediate disposal after Ready=false, unlike remote state notifications.
        _notifications.Writer.TryWrite(() => Status?.Invoke("IME_DIAG bridge.startup stage=" + stage));
    }

    private void Fail(CandidateObserverFailure failure, Process? bridge = null)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        string childExit = "not-started";
        if (bridge is not null)
        {
            try
            {
                if (bridge.HasExited)
                {
                    failure = failure with { ChildExitCode = bridge.ExitCode };
                    childExit = bridge.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                else childExit = "running";
            }
            catch (Exception) { childExit = "unavailable"; }
        }
        Volatile.Write(ref _failure, failure);
        string diagnostic = $"IME_DIAG bridge.failed stage={failure.Stage};childExitCode={childExit};code={failure.Code};error={failure.ErrorType};hresult={failure.HResult};nativeError={failure.NativeErrorCode}";
        Volatile.Write(ref _failureDiagnostic, diagnostic);
        lock (_gate)
        {
            _running = false; _enabled = false;
            InvalidateLocked();
            QueueFocusLocked();
            QueueStatusLocked("候选观察器不可用：" + failure.ToUserMessage());
            // A terminal diagnostic must not be dropped when the awaiting owner
            // observes Ready=false and disposes the failed binding immediately.
            _notifications.Writer.TryWrite(() => Status?.Invoke(diagnostic));
        }
        _ready.TrySetResult(false);
        _ = _lifetime.CancelAsync();
    }

    private async Task DrainNotificationsAsync()
    {
        await foreach (var notification in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
            try { notification(); } catch (Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate) { _enabled = false; _running = false; _epoch++; _inputSequence++; InvalidateLocked(); }
        _ready.TrySetResult(false);
        _commands.Writer.TryComplete();
        _ = _lifetime.CancelAsync();
        _notifications.Writer.TryComplete();
    }
}
