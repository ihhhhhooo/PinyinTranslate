using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PinyinTranslate.App;
using PinyinTranslate.Input;

namespace PinyinTranslate.Screenshot.Tests;

internal sealed record HeadlessClipboardCopyRequest(string Text, Func<bool> StillCurrent, CancellationToken CancellationToken);

/// <summary>
/// Exercises runtime state with managed fakes. The runtime constructor and Dispose
/// are deliberately not called: they own UIA workers and global input hooks.
/// </summary>
internal sealed class HeadlessRuntimeFixture : IDisposable
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly object _resourcesGate = new();
    private readonly List<(Process Process, ICandidateObserver Observer)> _resources = [];
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public Type RuntimeType { get; }
    public object Runtime { get; }
    public CandidateTranslationCoordinator Coordinator { get; }
    public ConcurrentQueue<Exception> Faults { get; } = new();
    public ConcurrentQueue<HeadlessClipboardCopyRequest> CopyRequests { get; } = new();
    public ITranslationClipboardWriter ClipboardWriter { get; }
    public EditableContextSnapshot? Context { get; set; }
    public nint ForegroundWindow { get; set; } = 1;
    public Func<string, CancellationToken, Task<string>> TranslateAsync { get; set; } = (_, _) => Task.FromResult("English");
    public Func<CancellationToken, Task<EditableContextSnapshot?>> ReadContextAsync { get; set; }
    public Func<string, Func<bool>, CancellationToken, Task<TranslationClipboardCopyResult>> CopyAsync { get; set; }
        = (_, _, _) => Task.FromResult(new TranslationClipboardCopyResult(false, false, "HeadlessCopyDenied"));
    public Func<EditableContextSnapshot, BaselineAnchor?, string, CancellationToken, Func<bool>, Func<bool>?, Task<EditableReplaceResult>> ReplaceAsync { get; set; }
        = (_, _, _, _, _, _) => Task.FromResult(new EditableReplaceResult(false, false, "HeadlessDenied"));

    public HeadlessRuntimeFixture(Func<Process, string, string, ICandidateObserver>? observerFactory = null, Type? runtimeType = null,
        bool copyOnly = false, ITranslationClipboardWriter? clipboardWriter = null)
    {
        RuntimeType = runtimeType ?? typeof(DesktopServices).Assembly.GetType("PinyinTranslate.App.ImeRuntimeController", true)!;
        Runtime = RuntimeHelpers.GetUninitializedObject(RuntimeType);
        ClipboardWriter = clipboardWriter ?? new DelegateClipboardWriter(this);
        ReadContextAsync = _ => Task.FromResult(Context);
        Set("_gate", new object());
        Set("_diagnosticGate", new object());
        Set("_pollCancellation", _lifetime);
        // Keep testProcessId null, so runtime diagnostics cannot write local logs.
        ICandidateObserver CreateObserver(Process process, string id, string runtimeId)
        {
            var observer = observerFactory?.Invoke(process, id, runtimeId) ?? new InertObserver();
            lock (_resourcesGate) _resources.Add((process, observer));
            return observer;
        }
        object factory = RuntimeType.GetField("_observerFactory", InstanceFields)!.FieldType == typeof(Func<Process, string, string, ICandidateObserver>)
            ? new Func<Process, string, string, ICandidateObserver>(CreateObserver)
            : new Func<Process, EditableContextSnapshot, ICandidateObserver>((process, context) => CreateObserver(process, context.AutomationId, context.FocusRuntimeId));
        Set("_observerFactory", factory);
        Set("_readContextAsync", new Func<CancellationToken, Task<EditableContextSnapshot?>>(token => ReadContextAsync(token)));
        Set("_foregroundWindow", new Func<nint>(() => ForegroundWindow));
        Set("_replaceAsync", new Func<EditableContextSnapshot, BaselineAnchor?, string, CancellationToken, Func<bool>, Func<bool>?, Task<EditableReplaceResult>>(
            (context, anchor, text, token, current, finishCaret) => ReplaceAsync(context, anchor, text, token, current, finishCaret)));
        // Legacy binary probes default to false and never write fields absent
        // from the old runtime. Copy-only tests opt into a managed fake writer.
        if (copyOnly)
        {
            Set("_copyOnly", true);
            Set("_clipboardWriter", ClipboardWriter);
        }
        Func<CandidateObservation, string, CancellationToken, Task<bool>>? acceptor = copyOnly ? null :
            (candidate, text, token) => InvokeAsync<bool>("AcceptAsync", candidate, text, token);
        Coordinator = new((candidate, token) => TranslateAsync(candidate.Candidate, token),
            (candidate, token) => InvokeAsync<bool>("ValidateAsync", candidate, token), acceptor);
        Set("_coordinator", Coordinator);
        Coordinator.Changed += state => Invoke("OnStateChanged", state);
        Coordinator.Faulted += Faults.Enqueue;
        if (copyOnly) Coordinator.Faulted += error => Invoke("OnCoordinatorFaulted", error);
    }

    public void Enable(long epoch = 1)
    {
        Set("_enabled", 1);
        Set("_epoch", epoch);
        Coordinator.SetEnabled(true);
    }

    public void SetContext(EditableContextSnapshot context)
    {
        Context = context;
        ForegroundWindow = context.ForegroundWindow;
        Set("_context", context);
        Set("_contextReadAt", Stopwatch.GetTimestamp());
    }

    public object? Get(string name) => Field(name).GetValue(Runtime);
    public T Get<T>(string name) => (T)Get(name)!;
    public void Set(string name, object? value) => Field(name).SetValue(Runtime, value);

    public object? Invoke(string name, params object?[] arguments)
    {
        // These entry points require real lifetime-owned native services.
        if (name is "SetEnabledAsync" or "PollAsync" or "Dispose")
            throw new InvalidOperationException("A headless fixture cannot invoke native lifetime entry points.");
        var method = RuntimeType.GetMethod(name, InstanceFields) ?? throw new MissingMethodException(RuntimeType.FullName, name);
        try { return method.Invoke(Runtime, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    public Task InvokeAsync(string name, params object?[] arguments) => (Task)Invoke(name, arguments)!;
    public Task<T> InvokeAsync<T>(string name, params object?[] arguments) => (Task<T>)Invoke(name, arguments)!;

    private FieldInfo Field(string name) => RuntimeType.GetField(name, InstanceFields) ?? throw new MissingFieldException(RuntimeType.FullName, name);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Set("_disposed", 1);
        Set("_enabled", 0);
        _lifetime.Cancel();
        Coordinator.Dispose();
        ClipboardWriter.Dispose();
        (Process Process, ICandidateObserver Observer)[] resources;
        lock (_resourcesGate) { resources = _resources.ToArray(); _resources.Clear(); }
        foreach (var resource in resources)
        {
            resource.Observer.Dispose();
            // Only Process.GetProcessById metadata is owned; no process is started.
            resource.Process.Dispose();
        }
        _lifetime.Dispose();
    }

    private sealed class DelegateClipboardWriter(HeadlessRuntimeFixture fixture) : ITranslationClipboardWriter
    {
        private int _disposed;
        public Task<TranslationClipboardCopyResult> CopyAsync(string text, Func<bool> stillCurrent, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return Task.FromResult(new TranslationClipboardCopyResult(false, false, "HeadlessWriterDisposed"));
            fixture.CopyRequests.Enqueue(new(text, stillCurrent, cancellationToken));
            return fixture.CopyAsync(text, stillCurrent, cancellationToken);
        }
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class InertObserver : ICandidateObserver
    {
        public Task<bool> Ready => Task.FromResult(true);
        public bool SetEnabled(bool enabled) => true;
        public bool IsCurrent(long version) => false;
        public void NotifyInputChanged() { }
        public Task<bool> ValidateAsync(CandidateObservation snapshot, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public event Action<long>? Invalidated { add { } remove { } }
        public event Action? FocusInvalidated { add { } remove { } }
        public event Action<CandidateObservation>? Observed { add { } remove { } }
        public event Action<long>? CandidatesCleared { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
        public void Dispose() { }
    }
}
