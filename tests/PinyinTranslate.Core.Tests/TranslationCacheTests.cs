using PinyinTranslate.Core;

namespace PinyinTranslate.Core.Tests;

public sealed class TranslationCacheTests
{
    private static TaskCompletionSource<string> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<string> Get(TranslationCache cache, string text, Func<CancellationToken, Task<string>> factory, CancellationToken token = default,
        string model = "model-a", string direction = "en-zh", string prompt = "v1") =>
        cache.GetOrTranslateAsync(model, direction, prompt, text, factory, token);

    [Fact]
    public async Task SimultaneousSubscribersShareOneOperationAndLaterHitCache()
    {
        using var cache = new TranslationCache(); var pending = Pending(); int calls = 0;
        Task<string> Factory(CancellationToken _) { Interlocked.Increment(ref calls); return pending.Task; }
        var first = Get(cache, "hello", Factory); var second = Get(cache, "hello", Factory);
        Assert.Equal(1, calls); pending.SetResult("你好");
        Assert.Equal("你好", await first); Assert.Equal("你好", await second);
        Assert.Equal("你好", await Get(cache, "hello", Factory)); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OneCancelledSubscriberDoesNotCancelSharedOperation()
    {
        using var cache = new TranslationCache(); using var cancel = new CancellationTokenSource();
        var pending = Pending(); CancellationToken underlying = default;
        Task<string> Factory(CancellationToken token) { underlying = token; return pending.Task; }
        var first = Get(cache, "hello", Factory, cancel.Token); var second = Get(cache, "hello", Factory);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(underlying.IsCancellationRequested);
        pending.SetResult("你好"); Assert.Equal("你好", await second);
    }

    [Fact]
    public async Task LastCancelledSubscriberCancelsUnderlyingOperation()
    {
        using var cache = new TranslationCache(); using var firstCancel = new CancellationTokenSource(); using var secondCancel = new CancellationTokenSource();
        var pending = Pending(); CancellationToken underlying = default;
        Task<string> Factory(CancellationToken token) { underlying = token; return pending.Task; }
        var first = Get(cache, "hello", Factory, firstCancel.Token); var second = Get(cache, "hello", Factory, secondCancel.Token);
        firstCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.False(underlying.IsCancellationRequested);
        secondCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); Assert.True(underlying.IsCancellationRequested);
        pending.SetResult("late");
    }

    [Fact]
    public async Task OlderCancelledFlightCannotRemoveOrCacheOverNewerFlight()
    {
        using var cache = new TranslationCache(); using var cancel = new CancellationTokenSource();
        var old = Pending(); var current = Pending(); int calls = 0;
        Task<string> Factory(CancellationToken _) => Interlocked.Increment(ref calls) == 1 ? old.Task : current.Task;
        var oldWaiter = Get(cache, "same", Factory, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWaiter);
        var newWaiter = Get(cache, "same", Factory); Assert.Equal(2, calls);
        old.SetResult("stale");
        // Observe completion of the old ignored-cancellation factory before joining the new flight.
        await old.Task; await Task.Yield();
        var joiner = Get(cache, "same", Factory); Assert.Equal(2, calls);
        current.SetResult("fresh"); Assert.Equal("fresh", await newWaiter); Assert.Equal("fresh", await joiner);
        Assert.Equal("fresh", await Get(cache, "same", Factory)); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExceptionsAndEmptyOutputsAreNotCached()
    {
        using var cache = new TranslationCache(); int calls = 0;
        Task<string> Factory(CancellationToken _)
        {
            int n = Interlocked.Increment(ref calls);
            return n switch { 1 => Task.FromException<string>(new InvalidOperationException("mock")), 2 => Task.FromResult(""), _ => Task.FromResult("valid") };
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Get(cache, "x", Factory));
        Assert.Equal("", await Get(cache, "x", Factory));
        Assert.Equal("valid", await Get(cache, "x", Factory)); Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ModelDirectionPromptAndTextAllPartitionCache()
    {
        using var cache = new TranslationCache(); int calls = 0;
        Task<string> Factory(CancellationToken _) => Task.FromResult((++calls).ToString());
        Assert.Equal("1", await Get(cache, "a", Factory));
        Assert.Equal("2", await Get(cache, "a", Factory, model: "model-b"));
        Assert.Equal("3", await Get(cache, "a", Factory, direction: "zh-en"));
        Assert.Equal("4", await Get(cache, "a", Factory, prompt: "v2"));
        Assert.Equal("5", await Get(cache, "b", Factory));
        Assert.Equal("1", await Get(cache, "a", Factory)); Assert.Equal(5, calls);
    }

    [Fact]
    public async Task CapacityEvictsLeastRecentlyUsedItem()
    {
        using var cache = new TranslationCache(capacity: 2); int calls = 0;
        Task<string> Factory(CancellationToken _) => Task.FromResult((++calls).ToString());
        Assert.Equal("1", await Get(cache, "a", Factory)); Assert.Equal("2", await Get(cache, "b", Factory));
        Assert.Equal("1", await Get(cache, "a", Factory)); Assert.Equal("3", await Get(cache, "c", Factory));
        Assert.Equal("1", await Get(cache, "a", Factory)); Assert.Equal("4", await Get(cache, "b", Factory));
    }

    [Fact]
    public async Task ConcurrentCallersOnlyStartOneFactory()
    {
        using var cache = new TranslationCache(); using var entered = new CountdownEvent(40);
        var pending = Pending(); int calls = 0;
        Task<string> Factory(CancellationToken _) { Interlocked.Increment(ref calls); return pending.Task; }
        var callers = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            var result = Get(cache, "same", Factory);
            entered.Signal();
            return await result;
        })).ToArray();
        Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
        Assert.Equal(1, calls); pending.SetResult("shared");
        Assert.All(await Task.WhenAll(callers), value => Assert.Equal("shared", value));
    }

    [Fact]
    public async Task CancelledFactoryDoesNotPoisonNextRequest()
    {
        using var cache = new TranslationCache(); int calls = 0;
        Task<string> Factory(CancellationToken _) => ++calls == 1
            ? Task.FromCanceled<string>(new CancellationToken(true)) : Task.FromResult("retry");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(cache, "x", Factory));
        Assert.Equal("retry", await Get(cache, "x", Factory)); Assert.Equal(2, calls);
    }
    private sealed class FakeTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }
    [Fact]
    public async Task ExpiredEntryIsNeverReturned()
    {
        var clock = new FakeTime(); using var cache = new TranslationCache(ttl: TimeSpan.FromSeconds(10), timeProvider: clock); int calls = 0;
        Task<string> Factory(CancellationToken _) => Task.FromResult((++calls).ToString());
        Assert.Equal("1", await Get(cache, "a", Factory)); clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("1", await Get(cache, "a", Factory)); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("2", await Get(cache, "a", Factory));
    }

    [Fact]
    public async Task DisposeDoesNotWaitForBlockingCancellationCallback()
    {
        var cache = new TranslationCache();
        using var callbackEntered = new ManualResetEventSlim(false);
        using var callbackRelease = new ManualResetEventSlim(false);
        var factoryResult = Pending();
        var waiting = Get(cache, "sensitive", token =>
        {
            token.Register(() => { callbackEntered.Set(); callbackRelease.Wait(); });
            return factoryResult.Task;
        });
        try
        {
            var dispose = Task.Run(cache.Dispose);
            Assert.True(await Task.Run(() => callbackEntered.Wait(TimeSpan.FromSeconds(5))));
            // Do not release the callback until after Dispose returns: synchronous
            // CancellationTokenSource.Cancel() used to block this exact path.
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            callbackRelease.Set();
            factoryResult.TrySetResult("late");
            cache.Dispose();
        }
    }
    [Fact]
    public async Task DisposeCancelsWaitersEvenWhenFactoryIgnoresCancellation()
    {
        var cache = new TranslationCache(); var pending = Pending();
        var waiting = Get(cache, "x", _ => pending.Task); cache.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Throws<ObjectDisposedException>(() => { _ = Get(cache, "x", _ => pending.Task); });
        pending.SetResult("late");
    }
}
