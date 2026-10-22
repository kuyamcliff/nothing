using ClipFx.Model;
using ClipFx.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClipFx.Engine;

/// <summary>Feeds traffic into the engine, ticks live rates and saves state.</summary>
public sealed class TrafficMonitor : BackgroundService
{
    static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);
    static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(5);

    readonly UsageEngine _engine;
    readonly ITrafficSource _source;
    readonly IProcessResolver _resolver;
    readonly StateStore _store;
    readonly SnapshotHub _hub;
    readonly ILogger<TrafficMonitor> _log;
    readonly object _pendingGate = new();
    readonly Dictionary<int, Pending> _pending = new();

    sealed class Pending { public long Up, Down; public DateTime Since; }

    public TrafficMonitor(UsageEngine engine, PlatformServices platform, StateStore store, SnapshotHub hub, ILogger<TrafficMonitor> log)
    {
        _engine = engine;
        _source = platform.Traffic;
        _resolver = platform.Resolver;
        _store = store;
        _hub = hub;
        _log = log;
        _engine.Changed += _hub.Notify;
    }

    public bool IsRunning => _source.IsRunning;
    public string? Error => _source.Error;

    public override Task StartAsync(CancellationToken ct)
    {
        _source.Traffic += OnTraffic;
        _source.ProcessStarted += (pid, path) => { _resolver.Hint(pid, path); FlushPending(pid); };
        _source.ProcessStopped += pid => _resolver.Exited(pid);
        try { _source.Start(); }
        catch (Exception ex) { _log.LogError(ex, "Traffic monitor failed to start"); }
        return base.StartAsync(ct);
    }

    public void OnTraffic(TrafficEvent e)
    {
        if (!AddressFilter.IsInternet(e.Remote)) return;
        var id = _resolver.Resolve(e.Pid);
        if (id != null) { _engine.Record(id, e.Pid, e.Bytes, e.Sent); return; }
        lock (_pendingGate)
        {
            if (!_pending.TryGetValue(e.Pid, out var p)) _pending[e.Pid] = p = new Pending { Since = DateTime.UtcNow };
            if (e.Sent) p.Up += e.Bytes; else p.Down += e.Bytes;
        }
    }

    void FlushPending(int pid)
    {
        Pending? p;
        lock (_pendingGate) { if (!_pending.Remove(pid, out p)) return; }
        var id = _resolver.Resolve(pid) ?? AppIdentity.Unknown;
        _engine.Record(id, pid, p.Up, true);
        _engine.Record(id, pid, p.Down, false);
    }

    void RetryPending()
    {
        List<int> pids;
        lock (_pendingGate) pids = _pending.Keys.ToList();
        foreach (var pid in pids)
        {
            Pending? p;
            AppIdentity? id = _resolver.Resolve(pid);
            lock (_pendingGate)
            {
                if (!_pending.TryGetValue(pid, out p)) continue;
                if (id == null && DateTime.UtcNow - p.Since < GiveUpAfter) continue;
                _pending.Remove(pid);
            }
            id ??= AppIdentity.Unknown;
            _engine.Record(id, pid, p.Up, true);
            _engine.Record(id, pid, p.Down, false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastSave = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    RetryPending();
                    _engine.Tick();
                    if (DateTime.UtcNow - lastSave >= SaveEvery && _engine.IsDirty)
                    {
                        Save();
                        lastSave = DateTime.UtcNow;
                    }
                }
                catch (Exception ex) { _log.LogError(ex, "Monitor tick failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Save()
    {
        try { _store.Save(_engine.Serialize()); }
        catch (Exception ex) { _log.LogError(ex, "Saving state failed"); }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        _source.Dispose();
        Save();
    }
}
