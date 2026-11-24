namespace InventoryService.Tests.TestDoubles;

// A clock where every wait finishes immediately, so retry tests run instantly.
// It records each requested delay, so tests can still check how long the client
// *would* have waited.
public sealed class InstantTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> _delays = new();

    public IReadOnlyList<TimeSpan> Delays
    {
        get { lock (_delays) return _delays.ToList(); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_delays) _delays.Add(dueTime);

        // Fire on the thread pool rather than inline, the way a real timer would.
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new NoOpTimer();
    }

    private sealed class NoOpTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
