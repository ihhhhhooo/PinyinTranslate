using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PinyinTranslate.Selection;

/// <summary>Serializes selection reads, range validation and fresh-selection barriers.</summary>
public sealed class SelectionMonitor : ISelectionMonitor
{
    private readonly object gate = new();
    private readonly ISelectionReader reader;
    private readonly ISelectionActivityMonitor? activity;
    private readonly SelectionMonitorOptions options;
    private readonly Action<string>? diagnostic;
    private readonly BlockingCollection<Action> work = new(128);
    private readonly Channel<Action> notices = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread worker;
    private readonly Timer debounce, poll;
    private long epoch, version, activityEpoch, handledActivityRevision;
    private bool enabled, disposed, readQueued, debouncePending, readAgain, freshBlocked, freshBaselineCaptured, freshActivity;
    private bool pointerGestureInProgress, keyboardGestureInProgress;
    private bool gestureInProgress => pointerGestureInProgress || keyboardGestureInProgress;
    private bool completedGestureRead, emptyGestureNotified;
    private SelectionReadResult? freshBaseline;
    private Entry? current;
    private SelectionReadOrigin origin;
    private int? pointerX, pointerY;
    private nint activityWindow;
    private sealed record Entry(SelectionReadResult Evidence, SelectionSnapshot Snapshot);

    public event Action<long>? Invalidated;
    public event Action<SelectionSnapshot>? SelectionChanged;
    public event Action<string>? StatusChanged;
    public Task<bool> Ready => ready.Task;

    public SelectionMonitor(Action<string>? diagnostic = null)
        : this(new UiaSelectionReader(), new SelectionActivityMonitor(), null, diagnostic, true) { }

    /// <summary>Injected readers/activity sources allow the actual monitor to run without native services.</summary>
    public SelectionMonitor(ISelectionReader reader, ISelectionActivityMonitor? activity = null,
        SelectionMonitorOptions? options = null, Action<string>? diagnostic = null)
        : this(reader, activity, options, diagnostic, false) { }

    private SelectionMonitor(ISelectionReader reader, ISelectionActivityMonitor? activity,
        SelectionMonitorOptions? options, Action<string>? diagnostic, bool nativeWorker)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.activity = activity;
        this.options = options ?? new();
        this.diagnostic = diagnostic;
        if (this.options.MaximumTextLength <= 0 || this.options.ValidationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        reader.Changed += ReaderChanged;
        if (activity is not null) activity.Activity += ActivityChanged;
        debounce = new Timer(_ => QueueRead(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        poll = new Timer(_ => ScheduleRead(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _ = Task.Run(DrainNoticesAsync);
        worker = new Thread(Run) { IsBackground = true, Name = "Selection reader MTA" };
        if (nativeWorker) worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    public void SetEnabled(bool value, bool requireFreshSelection = false)
    {
        long captured;
        lock (gate)
        {
            if (disposed) return;
            enabled = value;
            captured = ++epoch;
            InvalidateLocked();
            freshBlocked = value && requireFreshSelection;
            freshBaselineCaptured = freshActivity = false;
            pointerGestureInProgress = keyboardGestureInProgress = false;
            completedGestureRead = emptyGestureNotified = false;
            freshBaseline = null;
            debouncePending = readAgain = false;
            debounce.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            origin = SelectionReadOrigin.Enable;
            pointerX = pointerY = null;
            activityWindow = 0;
            activity?.SetEnabled(value);
            activityEpoch = activity?.Epoch ?? 0;
            handledActivityRevision = activity?.EpochStartRevision ?? 0;
            poll.Change(value && options.PollInterval > TimeSpan.Zero ? options.PollInterval : Timeout.InfiniteTimeSpan,
                value && options.PollInterval > TimeSpan.Zero ? options.PollInterval : Timeout.InfiniteTimeSpan);
        }
        TryQueue(() =>
        {
            lock (gate) if (disposed || epoch != captured) return;
            reader.SetEnabled(value, captured);
        });
        if (value) ScheduleRead();
    }

    public bool IsCurrent(long candidate)
    {
        lock (gate) return IsCurrentLocked(candidate);
    }

    private bool IsCurrentLocked(long candidate) => !disposed && enabled && !freshBlocked && !gestureInProgress &&
        current is not null && candidate == version && current.Snapshot.Version == candidate &&
        current.Snapshot.ActivityRevision == (activity?.Revision ?? 0) && current.Snapshot.ActivityRevision == handledActivityRevision;

    public async Task<bool> ValidateAsync(SelectionSnapshot snapshot)
    {
        Entry entry;
        lock (gate)
        {
            if (!IsCurrentLocked(snapshot.Version) || current!.Snapshot != snapshot) return false;
            entry = current;
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int expired = 0;
        if (!TryQueue(() =>
        {
            if (Volatile.Read(ref expired) != 0) { completion.TrySetResult(false); return; }
            bool valid;
            try { valid = IsCurrent(snapshot.Version) && reader.Validate(entry.Evidence); }
            catch (Exception error) { Trace("validate=" + error.GetType().Name); valid = false; }
            lock (gate)
            {
                valid = valid && ReferenceEquals(current, entry) && IsCurrentLocked(snapshot.Version);
                if (!valid && ReferenceEquals(current, entry)) InvalidateLocked();
            }
            completion.TrySetResult(valid);
        }))
        {
            RevokeValidation(entry, "选区复核队列繁忙，本轮已结束；请重新划选。");
            return false;
        }
        try { return await completion.Task.WaitAsync(options.ValidationTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Interlocked.Exchange(ref expired, 1);
            RevokeValidation(entry, "页面选区复核超时，本轮已结束；可重新划选或使用截图翻译。");
            return false;
        }
    }

    private void RevokeValidation(Entry entry, string message)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, entry)) return;
            InvalidateLocked();
            long captured = version;
            Notice(() =>
            {
                lock (gate) if (disposed || !enabled || version != captured || current is not null) return;
                Invoke(StatusChanged, message);
            });
        }
    }

    /// <summary>Schedules one serialized read; useful to deterministic injected environments.</summary>
    public async Task RequestReadAsync(SelectionReadOrigin requestedOrigin = SelectionReadOrigin.Poll)
    {
        long captured;
        lock (gate)
        {
            if (disposed || !enabled) return;
            captured = epoch;
            if (requestedOrigin != SelectionReadOrigin.Poll) origin = requestedOrigin;
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryQueue(() => { try { Read(captured); } finally { completion.TrySetResult(true); } })) return;
        await completion.Task.WaitAsync(options.ValidationTimeout).ConfigureAwait(false);
    }

    private void ReaderChanged(SelectionReaderSignal signal)
    {
        lock (gate)
        {
            if (disposed || !enabled || signal.Epoch != epoch) return;
            // Events are read hints, not proof that range endpoints changed.
            // Physical activity revokes immediately through its local revision.
            origin = signal.Kind == SelectionReaderChangeKind.Focus ? SelectionReadOrigin.Focus : SelectionReadOrigin.SelectionEvent;
        }
        ScheduleRead();
    }

    private void ActivityChanged(SelectionActivity change)
    {
        lock (gate)
        {
            if (disposed || !enabled || activity is null || change.Epoch != activityEpoch || change.Epoch != activity.Epoch) return;
            handledActivityRevision = Math.Max(handledActivityRevision, change.Revision);
            InvalidateLocked();
            if (change.Kind is SelectionActivityKind.PointerDown or SelectionActivityKind.KeyDown or SelectionActivityKind.PointerWheel)
                completedGestureRead = freshActivity = false;
            bool pointer = change.Kind is SelectionActivityKind.PointerDown or SelectionActivityKind.PointerUp or SelectionActivityKind.PointerWheel;
            activityWindow = change.TargetWindow;
            if (pointer) { pointerX = change.X; pointerY = change.Y; origin = SelectionReadOrigin.Pointer; }
            else { pointerX = pointerY = null; origin = SelectionReadOrigin.Keyboard; }
            if (change.Kind == SelectionActivityKind.PointerDown) pointerGestureInProgress = true;
            if (change.Kind == SelectionActivityKind.KeyDown && IsSelectionKey(change)) keyboardGestureInProgress = true;
            if (change.Kind is SelectionActivityKind.PointerUp or SelectionActivityKind.KeyUp)
            {
                // A key-up from another target can end its keyboard suppression
                // without proving a fresh selection or releasing a pointer drag.
                if (change.Kind == SelectionActivityKind.PointerUp) pointerGestureInProgress = false;
                else if (change.CompletesSelectionGesture || change.VirtualKey is >= 0x21 and <= 0x28 or 0x41)
                    keyboardGestureInProgress = false;
                if (change.CompletesSelectionGesture) { freshActivity = true; completedGestureRead = true; }
            }
        }
        ScheduleRead();
    }

    private static bool IsSelectionKey(SelectionActivity change) =>
        change.CompletesSelectionGesture ||
        ((change.Modifiers & SelectionActivityModifiers.Shift) != 0 && change.VirtualKey is >= 0x21 and <= 0x28) ||
        ((change.Modifiers & SelectionActivityModifiers.Control) != 0 && change.VirtualKey == 0x41);

    private void ScheduleRead()
    {
        lock (gate)
        {
            if (disposed || !enabled || gestureInProgress) return;
            if (debouncePending) return;
            debouncePending = true;
            debounce.Change(options.Debounce < TimeSpan.Zero ? TimeSpan.Zero : options.Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void QueueRead()
    {
        long captured;
        lock (gate)
        {
            debouncePending = false;
            if (disposed || !enabled || gestureInProgress) return;
            if (readQueued) { readAgain = true; return; }
            readQueued = true;
            captured = epoch;
        }
        if (!TryQueue(() =>
        {
            try { Read(captured); }
            finally
            {
                bool again;
                lock (gate) { readQueued = false; again = readAgain; readAgain = false; }
                if (again) ScheduleRead();
            }
        }))
            lock (gate) readQueued = false;
    }

    private void Read(long capturedEpoch)
    {
        SelectionReadRequest request;
        long capturedVersion, capturedRevision;
        bool capturedCompletion;
        lock (gate)
        {
            if (disposed || !enabled || epoch != capturedEpoch || gestureInProgress || (activity?.Revision ?? 0) != handledActivityRevision) return;
            capturedVersion = version;
            capturedRevision = activity?.Revision ?? 0;
            capturedCompletion = completedGestureRead;
            request = new(epoch, origin, pointerX, pointerY, activityWindow);
            origin = SelectionReadOrigin.Poll;
        }
        SelectionReadResult? result;
        try { result = reader.Read(request); }
        catch (Exception error) { Trace("read=" + error.GetType().Name); result = null; }
        if (result is not null && (result.ProcessId <= 0 || string.IsNullOrWhiteSpace(result.Text) ||
            result.Text.Length > options.MaximumTextLength || string.IsNullOrEmpty(result.RuntimeId) ||
            string.IsNullOrEmpty(result.RangeId) || string.IsNullOrEmpty(result.ScopeId))) result = null;
        lock (gate)
        {
            if (disposed || !enabled || epoch != capturedEpoch || version != capturedVersion ||
                capturedRevision != (activity?.Revision ?? 0) || capturedRevision != handledActivityRevision || gestureInProgress) return;
            if (result is null)
            {
                if (current is not null) InvalidateLocked();
                if (freshBlocked) { freshBaseline = null; freshBaselineCaptured = true; }
                if (capturedCompletion)
                {
                    completedGestureRead = false;
                    QueueEmptyGestureNoticeLocked(capturedEpoch);
                }
                return;
            }
            completedGestureRead = emptyGestureNotified = false;
            if (freshBlocked)
            {
                bool completedHere = freshActivity &&
                    (activityWindow == 0 || unchecked((int)activityWindow.ToInt64()) == result.WindowHandle);
                if (!completedHere && (!freshBaselineCaptured || freshBaseline is null || !SameSource(freshBaseline, result)))
                { freshBaseline = result; freshBaselineCaptured = true; return; }
                if (!completedHere && Same(freshBaseline, result)) return;
                freshBlocked = freshActivity = false;
            }
            if (current is not null && current.Snapshot.ActivityRevision == capturedRevision && Same(current.Evidence, result)) return;
            if (current is not null) InvalidateLocked();
            var snapshot = new SelectionSnapshot(version, result.ProcessId, result.WindowHandle, result.RuntimeId, result.Text)
                { RangeId = result.RangeId, ScopeId = result.ScopeId, ActivityRevision = capturedRevision };
            current = new(result, snapshot);
            Notice(() => { if (IsCurrent(snapshot.Version)) Invoke(SelectionChanged, snapshot); });
        }
    }

    private static bool Same(SelectionReadResult? left, SelectionReadResult? right) =>
        left is null ? right is null : right is not null && left.ProcessId == right.ProcessId &&
        left.WindowHandle == right.WindowHandle && left.RuntimeId == right.RuntimeId && left.Text == right.Text &&
        left.RangeId == right.RangeId && left.ScopeId == right.ScopeId;
    private static bool SameSource(SelectionReadResult left, SelectionReadResult right) =>
        left.ProcessId == right.ProcessId && left.WindowHandle == right.WindowHandle &&
        left.RuntimeId == right.RuntimeId && left.ScopeId == right.ScopeId;

    private void QueueEmptyGestureNoticeLocked(long capturedEpoch)
    {
        if (emptyGestureNotified) return;
        emptyGestureNotified = true;
        long capturedVersion = version;
        Notice(() =>
        {
            lock (gate)
                if (disposed || !enabled || epoch != capturedEpoch || version != capturedVersion || current is not null || gestureInProgress) return;
            Invoke(StatusChanged, "未读取到可翻译的选中文字；如已划选，可尝试截图翻译。");
        });
    }

    private void InvalidateLocked()
    {
        long invalidated = ++version;
        current = null;
        Notice(() => Invoke(Invalidated, invalidated));
    }
    private bool TryQueue(Action action)
    {
        try { return work.TryAdd(action); } catch (InvalidOperationException) { return false; }
    }
    private void Run()
    {
        try
        {
            if (!reader.Start()) { ready.TrySetResult(false); return; }
            activity?.Start();
            ready.TrySetResult(true);
            Notice(() => Invoke(StatusChanged, "划词监控已就绪"));
            foreach (var action in work.GetConsumingEnumerable())
                try { action(); } catch (Exception error) { Trace("worker=" + error.GetType().Name); }
        }
        catch (Exception error) { Trace("start=" + error.GetType().Name); ready.TrySetResult(false); }
        finally { try { reader.Dispose(); } catch { } }
    }
    private void Notice(Action action) => notices.Writer.TryWrite(action);
    private async Task DrainNoticesAsync()
    {
        await foreach (var action in notices.Reader.ReadAllAsync()) try { action(); } catch { }
    }
    private static void Invoke<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList()) try { handler(value); } catch { }
    }
    private void Trace(string message) { try { diagnostic?.Invoke(message); } catch { } }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; enabled = false; ++epoch; InvalidateLocked();
        }
        debounce.Dispose(); poll.Dispose();
        reader.Changed -= ReaderChanged;
        if (activity is not null) { activity.Activity -= ActivityChanged; activity.Dispose(); }
        work.CompleteAdding();
        if (Thread.CurrentThread != worker) worker.Join(TimeSpan.FromSeconds(1));
        notices.Writer.TryComplete(); ready.TrySetResult(false);
    }
}
