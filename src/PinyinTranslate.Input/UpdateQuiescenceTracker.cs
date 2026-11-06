namespace PinyinTranslate.Input;

/// <summary>
/// Tracks admitted factories and their owned resources. Pausing closes admission
/// before draining, including resources attached by an already-admitted factory.
/// It performs no operating-system work; stop delegates run outside its locks.
/// </summary>
public sealed class UpdateQuiescenceTracker
{
    private readonly object gate = new();
    private readonly HashSet<Operation> operations = [];
    private readonly HashSet<Resource> resources = [];
    private bool paused;

    public bool IsPaused { get { lock (gate) return paused; } }

    public IDisposable? TryBeginOperation()
    {
        lock (gate)
        {
            if (paused) return null;
            var operation = new Operation(this);
            operations.Add(operation);
            return operation;
        }
    }

    public Resource Register(Func<Task> stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        Resource resource;
        bool stopNow;
        lock (gate)
        {
            resource = new(this, stop);
            resources.Add(resource);
            stopNow = paused;
        }
        if (stopNow) _ = ObserveAsync(resource.StopAsync());
        return resource;
    }

    public void Pause() { lock (gate) paused = true; }
    public void Resume() { lock (gate) paused = false; }

    public async Task PauseAndDrainAsync(CancellationToken cancellationToken = default)
    {
        Pause();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operation[] pendingOperations;
            Resource[] pendingResources;
            lock (gate)
            {
                pendingOperations = operations.ToArray();
                pendingResources = resources.ToArray();
                if (pendingOperations.Length == 0 && pendingResources.Length == 0) return;
            }
            var waits = pendingOperations.Select(operation => operation.Completion)
                .Concat(pendingResources.Select(resource => resource.StopAsync()));
            await Task.WhenAll(waits).WaitAsync(cancellationToken).ConfigureAwait(false);
            // A factory may attach its resource immediately before completing.
            // Re-snapshot rather than assuming the first batch was exhaustive.
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch (Exception) { }
    }

    private sealed class Operation(UpdateQuiescenceTracker owner) : IDisposable
    {
        private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposed;
        public Task Completion => completion.Task;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.gate) owner.operations.Remove(this);
            completion.TrySetResult(true);
        }
    }

    public sealed class Resource
    {
        private readonly object gate = new();
        private readonly UpdateQuiescenceTracker owner;
        private readonly Func<Task> stop;
        private Task? stopping;
        private bool stopped;
        internal Resource(UpdateQuiescenceTracker owner, Func<Task> stop) { this.owner = owner; this.stop = stop; }

        public Task StopAsync()
        {
            lock (gate)
            {
                if (stopped) return Task.CompletedTask;
                if (stopping is { IsCompleted: false }) return stopping;
                // A failed stop remains owned and can be retried by a later update.
                stopping = Task.Run(async () =>
                {
                    await stop().ConfigureAwait(false);
                    lock (gate) stopped = true;
                    lock (owner.gate) owner.resources.Remove(this);
                });
                _ = ObserveAsync(stopping);
                return stopping;
            }
        }
    }
}
