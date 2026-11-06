using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using PinyinTranslate.Input;

internal static class Program
{
    [MTAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || args[0] != "--pipe" || args[2] != "--parent" || args[4] != "--session" ||
            !int.TryParse(args[3], out int parentPid) || parentPid <= 0 || args[1].Length > 160 || !Guid.TryParseExact(args[5], "N", out _)) return 2;
        using var lifetime = new CancellationTokenSource();
        NamedPipeClientStream? pipe = null;
        CandidateObserver? observer = null;
        BridgeSession? session = null;
        Task? writer = null;
        bool authenticated = false, readySent = false;
        string stage = "parent-query";
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(5));
            stage = "pipe-connect";
            await pipe.ConnectAsync(startup.Token).ConfigureAwait(false);
            if (!CandidateBridgeProtocol.IsPipeServer(pipe, parentPid)) return 3;
            var hello = await CandidateBridgeProtocol.ReadAsync(pipe, startup.Token).ConfigureAwait(false);
            if (hello.Kind != "hello" || hello.SessionId != args[5] || hello.ParentProcessId != parentPid || parent.HasExited ||
                hello.ParentStartTicks != parent.StartTime.ToUniversalTime().Ticks || hello.TargetProcessId <= 0 ||
                hello.TargetProcessId == parentPid || hello.TargetProcessId == Environment.ProcessId ||
                (hello.ForegroundScope is not null && (!hello.ForegroundScope.IsValid ||
                    hello.ForegroundScope.ProcessId == parentPid || hello.ForegroundScope.ProcessId == Environment.ProcessId)) ||
                (string.IsNullOrWhiteSpace(hello.AutomationId) && string.IsNullOrWhiteSpace(hello.FocusRuntimeId))) return 3;
            authenticated = true;
            stage = "target-query";
            using var target = Process.GetProcessById(hello.TargetProcessId);
            if (target.HasExited || target.StartTime.ToUniversalTime().Ticks != hello.TargetStartTicks)
            {
                var failure = new CandidateObserverFailure(target.HasExited ? CandidateObserverFailureCode.TargetExited :
                    CandidateObserverFailureCode.ProtocolError, "target-identity");
                await CandidateBridgeProtocol.WriteAsync(pipe, new() { Kind = "ready", SessionId = hello.SessionId,
                    WorkerHealthVersion = 2, Success = false, Failure = failure }, startup.Token).ConfigureAwait(false);
                return 3;
            }
            stage = "uia-create";
            observer = new CandidateObserver(target, hello.AutomationId, string.IsNullOrEmpty(hello.FocusRuntimeId) ? null : hello.FocusRuntimeId,
                hello.ForegroundScope);
            session = new BridgeSession(observer, hello.SessionId, lifetime);
            stage = "uia-ready";
            bool ready = await observer.Ready.WaitAsync(TimeSpan.FromSeconds(4), startup.Token).ConfigureAwait(false);
            await CandidateBridgeProtocol.WriteAsync(pipe, new() { Kind = "ready", SessionId = hello.SessionId, WorkerHealthVersion = 2, Success = ready,
                Failure = ready ? null : observer.Failure ?? new(CandidateObserverFailureCode.ObserverInitializationFailed, stage) }, startup.Token).ConfigureAwait(false);
            readySent = true;
            if (!ready) return 4;
            stage = "running";
            writer = session.WriteAsync(pipe, lifetime.Token);
            Task reader = session.ReadAsync(pipe, lifetime.Token);
            Task heartbeat = session.HeartbeatAsync(lifetime.Token);
            Task parentExit = parent.WaitForExitAsync(lifetime.Token);
            Task targetExit = target.WaitForExitAsync(lifetime.Token);
            await await Task.WhenAny(writer, reader, heartbeat, parentExit, targetExit).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            // Only a verified parent receives metadata. Sending a bounded failure
            // handshake preserves the real startup cause instead of just exit=5.
            if (authenticated && !readySent && pipe is { IsConnected: true })
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await CandidateBridgeProtocol.WriteAsync(pipe, new() { Kind = "ready", SessionId = args[5], WorkerHealthVersion = 2, Success = false,
                        Failure = CandidateObserverFailure.FromException(error, stage) }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
            else if (readySent && session is not null && writer is not null)
                await session.ReportFailureAsync(CandidateObserverFailure.FromException(error, stage), writer).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            return 5;
        }
        finally
        {
            // Keep the observer alive until the real terminal failure has been
            // sent. Dispose must not race a heartbeat into a false generic fault.
            await lifetime.CancelAsync().ConfigureAwait(false);
            observer?.Dispose();
            pipe?.Dispose();
        }
    }

    private sealed class BridgeSession
    {
        private readonly object _gate = new();
        private readonly CandidateObserver _observer;
        private readonly string _sessionId;
        private readonly CancellationTokenSource _lifetime;
        private readonly SemaphoreSlim _validationGate = new(1, 1);
        private readonly Channel<CandidateBridgeMessage> _outgoing = Channel.CreateBounded<CandidateBridgeMessage>(
            new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        private long _epoch, _inputSequence, _sourceFloor = long.MinValue;
        private bool _enabled;
        private TaskCompletionSource<bool>? _terminalWritten;

        public BridgeSession(CandidateObserver observer, string sessionId, CancellationTokenSource lifetime)
        {
            _observer = observer; _sessionId = sessionId; _lifetime = lifetime;
            observer.Invalidated += version => { lock (_gate) { if (version < _sourceFloor) return; _sourceFloor = version; SendLocked(new() { Kind = "invalidated", SourceVersion = version }); } };
            observer.FocusInvalidated += () => { lock (_gate) SendLocked(new() { Kind = "focus" }); };
            observer.Status += status => { lock (_gate) SendLocked(new() { Kind = "status", Status = status }); };
            observer.Observed += value =>
            {
                lock (_gate)
                {
                    if (!_enabled || !_observer.IsCurrent(value.Version) || value.Version < _sourceFloor) return;
                    SendLocked(new() { Kind = "observed", SourceVersion = value.Version, Observation = CandidateBridgeObservation.From(value) });
                }
            };
            observer.CandidatesCleared += version =>
            {
                lock (_gate)
                {
                    if (_enabled && _observer.IsCurrent(version) && version >= _sourceFloor) SendLocked(new() { Kind = "cleared", SourceVersion = version });
                }
            };
        }

        private void SendLocked(CandidateBridgeMessage message)
        {
            if (!_outgoing.Writer.TryWrite(message with { SessionId = _sessionId, Epoch = _epoch, InputSequence = _inputSequence }))
                _ = _lifetime.CancelAsync();
        }

        public async Task ReadAsync(Stream pipe, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                var command = await CandidateBridgeProtocol.ReadAsync(pipe, cancellation).ConfigureAwait(false);
                if (command.SessionId != _sessionId) throw new InvalidDataException("Session mismatch.");
                lock (_gate)
                {
                    switch (command.Kind)
                    {
                        case "enabled":
                            if (command.Epoch <= _epoch || command.InputSequence <= _inputSequence) throw new InvalidDataException("Nonmonotonic enabled barrier.");
                            _epoch = command.Epoch; _inputSequence = command.InputSequence; _enabled = command.Enabled;
                            // The sequence update and observer generation change are atomic
                            // relative to stamping outgoing observations on this same gate.
                            if (!_observer.SetEnabled(_enabled)) throw new InvalidOperationException("Observer is unavailable.");
                            break;
                        case "input":
                            if (command.Epoch != _epoch || command.InputSequence <= _inputSequence || !_enabled) throw new InvalidDataException("Invalid input barrier.");
                            _inputSequence = command.InputSequence;
                            _observer.NotifyInputChanged();
                            break;
                        case "validate":
                            _ = ValidateAsync(command, cancellation);
                            break;
                        default: throw new InvalidDataException("Unknown bridge command.");
                    }
                }
            }
        }

        private async Task ValidateAsync(CandidateBridgeMessage request, CancellationToken cancellation)
        {
            bool entered = false, valid = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(2400));
            try
            {
                await _validationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                entered = true;
                CandidateObservation? source;
                lock (_gate)
                {
                    source = IsRequestCurrent(request) ? request.Observation?.ToObservation(request.SourceVersion) : null;
                }
                if (source is not null) valid = await _observer.ValidateAsync(source, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { valid = false; }
            finally { if (entered) _validationGate.Release(); }
            lock (_gate)
            {
                // Reply with the request's original fence. A later input barrier
                // cannot give a completed validation a new sequence number.
                valid &= IsRequestCurrent(request);
                if (!_outgoing.Writer.TryWrite(new()
                {
                    Kind = "validated", SessionId = _sessionId, Epoch = request.Epoch, InputSequence = request.InputSequence,
                    RequestId = request.RequestId, SourceVersion = request.SourceVersion, Success = valid
                })) _ = _lifetime.CancelAsync();
            }
        }

        private bool IsRequestCurrent(CandidateBridgeMessage request) => _enabled && request.Epoch == _epoch &&
            request.InputSequence == _inputSequence && request.Observation?.SourceVersion == request.SourceVersion && _observer.IsCurrent(request.SourceVersion);

        public async Task WriteAsync(Stream pipe, CancellationToken cancellation)
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
            {
                await CandidateBridgeProtocol.WriteAsync(pipe, message, cancellation).ConfigureAwait(false);
                if (message.Kind == "failed") _terminalWritten?.TrySetResult(true);
            }
        }

        public async Task ReportFailureAsync(CandidateObserverFailure failure, Task writer)
        {
            Task flushed;
            lock (_gate)
            {
                if (writer.IsCompleted || _lifetime.IsCancellationRequested) return;
                _terminalWritten ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                flushed = _terminalWritten.Task;
                // All post-ready frames, including terminal failures, use the
                // single writer. Concurrent direct writes would corrupt framing.
                if (!_outgoing.Writer.TryWrite(new() { Kind = "failed", SessionId = _sessionId,
                    Epoch = _epoch, InputSequence = _inputSequence, Failure = failure })) return;
            }
            try { await flushed.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch (Exception) { /* Broken/full transports cannot deliver diagnostics. */ }
        }

        public async Task HeartbeatAsync(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellation).ConfigureAwait(false);
                lock (_gate) SendLocked(new() { Kind = "heartbeat", Success = _observer.IsRunning, Failure = _observer.Failure });
            }
        }
    }
}
