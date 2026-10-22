namespace ClipFx.Engine;

/// <summary>Wakes live streams when something changed.</summary>
public sealed class SnapshotHub
{
    TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object _gate = new();

    public void Notify()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _tcs;
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task t;
        lock (_gate) t = _tcs.Task;
        try { await t.WaitAsync(timeout, ct); }
        catch (TimeoutException) { }
    }
}
