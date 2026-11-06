namespace PinyinTranslate.Core;

public enum CacheDisposition { NewRequest, Cached, SharedRequest }

/// <summary>
/// Bounded, in-memory translation cache and in-flight deduplicator. Keys include
/// all inputs that affect the translation. A caller cancelling never cancels
/// another caller; the underlying operation is cancelled once its final waiter leaves.
/// </summary>
public sealed class TranslationCache : IDisposable
{
    private readonly record struct CacheKey(string Model, string Direction, string PromptVersion, string Text);
    private sealed class CacheItem(string value, long createdAt, long usedAt)
    {
        public string Value { get; } = value;
        public long CreatedAt { get; } = createdAt;
        public long UsedAt { get; set; } = usedAt;
    }
    private sealed class Flight
    {
        private readonly object _lifetimeGate = new();
        private Task? _cancellationCallbacks;
        private bool _finished;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters { get; set; }

        // CancelAsync marks the token cancelled immediately but dispatches untrusted
        // callbacks asynchronously, so a callback cannot block the UI exit path.
        public void RequestCancellation()
        {
            lock (_lifetimeGate)
            {
                if (_finished || _cancellationCallbacks != null) return;
                _cancellationCallbacks = ObserveCancellationAsync(Cancellation.CancelAsync());
            }
        }
        public void Finish()
        {
            Task? callbacks;
            lock (_lifetimeGate)
            {
                if (_finished) return;
                _finished = true;
                callbacks = _cancellationCallbacks;
            }
            if (callbacks is null) Cancellation.Dispose();
            else _ = DisposeAfterCallbacksAsync(callbacks, Cancellation);
        }
        private static async Task ObserveCancellationAsync(Task callbacks)
        {
            try { await callbacks.ConfigureAwait(false); }
            catch (Exception) { /* A cancellation callback cannot corrupt shared cache state. */ }
        }
        private static async Task DisposeAfterCallbacksAsync(Task callbacks, CancellationTokenSource source)
        {
            await callbacks.ConfigureAwait(false);
            source.Dispose();
        }
    }
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, CacheItem> _items = new();
    private readonly Dictionary<CacheKey, Flight> _flights = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly Timer _cleanup;
    private long _access;
    private bool _disposed;

    public TranslationCache(int capacity = 128, TimeSpan? ttl = null, TimeProvider? timeProvider = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        if (_ttl <= TimeSpan.Zero || _ttl > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(ttl));
        _capacity = capacity;
        _time = timeProvider ?? TimeProvider.System;
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_ttl.TotalMilliseconds / 2, 250, TimeSpan.FromMinutes(1).TotalMilliseconds));
        _cleanup = new Timer(_ => PruneExpired(), null, interval, interval);
    }

    /// <remarks>The factory is invoked once per active key and must honor its cancellation token where possible.</remarks>
    public Task<string> GetOrTranslateAsync(string model, string direction, string promptVersion, string text,
        Func<CancellationToken, Task<string>> factory, CancellationToken cancellationToken = default) =>
        GetOrTranslateAsync(model, direction, promptVersion, text, factory, out _, cancellationToken);

    public Task<string> GetOrTranslateAsync(string model, string direction, string promptVersion, string text,
        Func<CancellationToken, Task<string>> factory, out CacheDisposition disposition, CancellationToken cancellationToken = default)
    {
        disposition = CacheDisposition.NewRequest;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        var key = new CacheKey(model, direction, promptVersion, text);
        Flight flight;
        bool start = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long now = _time.GetTimestamp();
            if (_items.TryGetValue(key, out var item))
            {
                if (_time.GetElapsedTime(item.CreatedAt, now) < _ttl)
                {
                    item.UsedAt = ++_access;
                    disposition = CacheDisposition.Cached;
                    return Task.FromResult(item.Value);
                }
                _items.Remove(key);
            }
            if (!_flights.TryGetValue(key, out flight!))
            {
                flight = new Flight();
                _flights.Add(key, flight);
                start = true;
            }
            else disposition = CacheDisposition.SharedRequest;
            flight.Waiters++;
        }
        // Begin waiting before starting: synchronous factories and cancellation
        // still have an initialized subscriber and a stable in-flight identity.
        var waiting = WaitAsync(key, flight, cancellationToken);
        if (start) _ = RunAsync(key, flight, factory);
        return waiting;
    }

    private async Task<string> WaitAsync(CacheKey key, Flight flight, CancellationToken cancellationToken)
    {
        try { return await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            bool stop = false;
            lock (_gate)
            {
                flight.Waiters--;
                if (flight.Waiters == 0 && _flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                {
                    _flights.Remove(key);
                    stop = true;
                }
            }
            if (stop) flight.RequestCancellation();
        }
    }

    private async Task RunAsync(CacheKey key, Flight flight, Func<CancellationToken, Task<string>> factory)
    {
        try
        {
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            var value = await factory(flight.Cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_disposed && flight.Waiters > 0 && !flight.Cancellation.IsCancellationRequested &&
                    _flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                {
                    _flights.Remove(key);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        PruneExpiredLocked();
                        while (_items.Count >= _capacity)
                            _items.Remove(_items.MinBy(pair => pair.Value.UsedAt).Key);
                        _items[key] = new CacheItem(value, _time.GetTimestamp(), ++_access);
                    }
                }
            }
            flight.Completion.TrySetResult(value);
        }
        catch (OperationCanceledException e) { flight.Completion.TrySetCanceled(e.CancellationToken); }
        catch (Exception e) { flight.Completion.TrySetException(e); }
        finally
        {
            lock (_gate)
                if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
            // Observe a failure even when all waiters have already cancelled.
            if (flight.Completion.Task.IsFaulted) _ = flight.Completion.Task.Exception;
            flight.Finish();
        }
    }

    /// <summary>Remove expired data immediately; the timer also periodically prunes it.</summary>
    public void PruneExpired()
    {
        lock (_gate) { if (!_disposed) PruneExpiredLocked(); }
    }
    private void PruneExpiredLocked()
    {
        long now = _time.GetTimestamp();
        foreach (var key in _items.Where(pair => _time.GetElapsedTime(pair.Value.CreatedAt, now) >= _ttl).Select(pair => pair.Key).ToArray())
            _items.Remove(key);
    }

    public void Dispose()
    {
        Flight[] flights;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _items.Clear(); flights = _flights.Values.ToArray(); _flights.Clear();
        }
        _cleanup.Dispose();
        foreach (var flight in flights)
        {
            flight.Completion.TrySetCanceled(new CancellationToken(true));
            flight.RequestCancellation();
        }
    }
}
