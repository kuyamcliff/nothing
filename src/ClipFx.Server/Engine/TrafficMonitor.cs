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
        _counters = platform.Counters;
        _engine.Changed += _hub.Notify;
    }

    /// <summary>Records adapter bytes that no process accounts for, so the total matches the wire.</summary>
    void SettleUnattributed()
    {
        if (_counters is null) return;
        IReadOnlyList<InterfaceSample> samples;
        try { samples = _counters.Read(); }
        catch (Exception ex) { Diag.Note("counters.error", ex.Message); return; }
        _unattributed.Sample(samples);
        var r = _unattributed.Settle(Interlocked.Read(ref _attrUp), Interlocked.Read(ref _attrDown),
            Interlocked.Read(ref _localUp), Interlocked.Read(ref _localDown));
        _engine.Record(AppIdentity.OtherTraffic, 0, r.OtherUp, true);
        _engine.Record(AppIdentity.OtherTraffic, 0, r.OtherDown, false);
        _engine.Record(AppIdentity.VirtualMachines, 0, r.VmUp, true);
        _engine.Record(AppIdentity.VirtualMachines, 0, r.VmDown, false);
        Diag.Count("counters.other", r.OtherUp + r.OtherDown);
        Diag.Count("counters.vm", r.VmUp + r.VmDown);
    }

    void SettleUnattributedBaseline()
    {
        try { if (_counters != null) _unattributed.Sample(_counters.Read()); }
        catch (Exception ex) { Diag.Note("counters.error", ex.Message); }
    }

    public bool IsRunning => _source.IsRunning;
    public string? Error => _source.Error;

    public override Task StartAsync(CancellationToken ct)
    {
        _source.Traffic += OnTraffic;
        _source.ProcessStarted += (pid, path) => { _resolver.Hint(pid, path); FlushPending(pid); };
        _source.ProcessStopped += pid => _resolver.Exited(pid);
        try { SettleUnattributedBaseline(); _source.Start(); }
        catch (Exception ex) { _log.LogError(ex, "Traffic monitor failed to start"); }
        return base.StartAsync(ct);
    }

    // Cumulative bytes seen per process, used to find traffic no process accounts for.
    long _attrUp, _attrDown, _localUp, _localDown;
    readonly UnattributedTracker _unattributed = new();
    readonly IInterfaceCounters? _counters;

    public void OnTraffic(TrafficEvent e)
    {
        if (!AddressFilter.IsInternet(e.Remote))
        {
            Diag.Count("monitor.local");
            if (!AddressFilter.IsLoopback(e.Remote))
                Interlocked.Add(ref e.Sent ? ref _localUp : ref _localDown, e.Bytes);
            return;
        }
        Diag.Count("monitor.internet");
        Interlocked.Add(ref e.Sent ? ref _attrUp : ref _attrDown, e.Bytes);
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
            if (id == null) { Diag.Count("monitor.unknown"); id = AppIdentity.Unknown; }
            _engine.Record(id, pid, p.Up, true);
            _engine.Record(id, pid, p.Down, false);
        }
    }

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(MainLoop(ct), CountersLoop(ct));

    async Task CountersLoop(CancellationToken ct)
    {
        if (_counters is null) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { SettleUnattributed(); }
                catch (Exception ex) { _log.LogError(ex, "Adapter counters failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task MainLoop(CancellationToken ct)
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
