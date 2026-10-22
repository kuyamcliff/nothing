using System.Threading.Channels;
using ClipFx.Model;
using ClipFx.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClipFx.Engine;

/// <summary>Keeps firewall rules in line with what the engine says must be blocked.</summary>
public sealed class Enforcer : BackgroundService, IEnforcementState
{
    static readonly TimeSpan DriftCheck = TimeSpan.FromSeconds(10);

    readonly UsageEngine _engine;
    readonly IFirewall _fw;
    readonly IConnectionKiller _killer;
    readonly IProcessResolver _resolver;
    readonly SnapshotHub _hub;
    readonly ILogger<Enforcer> _log;
    readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly object _gate = new();
    readonly object _stateGate = new();
    HashSet<string> _applied = new(StringComparer.Ordinal);
    bool _global;

    public string? LastError { get; private set; }
    public bool FirewallEnabled { get; private set; } = true;

    public Enforcer(UsageEngine engine, PlatformServices platform, SnapshotHub hub, ILogger<Enforcer> log)
    {
        _engine = engine;
        _fw = platform.Firewall;
        _killer = platform.Killer;
        _resolver = platform.Resolver;
        _hub = hub;
        _log = log;
        _engine.PolicyChanged += Signal;
    }

    public void Signal() => _signal.Writer.TryWrite(true);

    public bool IsApplied(string key) { lock (_stateGate) return _applied.Contains(key); }
    public bool GlobalApplied { get { lock (_stateGate) return _global; } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Reconcile();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DriftCheck);
            try { await _signal.Reader.ReadAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Applies the difference between desired and actual firewall state.</summary>
    public void Reconcile()
    {
        lock (_gate)
        {
            var errors = new List<string>();
            var (desiredList, desiredGlobal) = _engine.Desired();
            var desired = desiredList.ToDictionary(i => i.Key, StringComparer.Ordinal);
            IReadOnlyDictionary<string, string> actual;
            try { actual = _fw.GetApplied(); }
            catch (Exception ex)
            {
                Fail("Can't read firewall rules: " + ex.Message, ex);
                return;
            }

            var appliedNow = new HashSet<string>(actual.Keys.Where(k => k != IFirewall.GlobalKey), StringComparer.Ordinal);
            bool globalNow = actual.ContainsKey(IFirewall.GlobalKey);
            var newlyBlocked = new List<AppIdentity>();

            foreach (var key in appliedNow.ToList())
            {
                if (desired.ContainsKey(key)) continue;
                try { _fw.Unblock(key); appliedNow.Remove(key); }
                catch (Exception ex) { errors.Add(ex.Message); _log.LogError(ex, "Unblock {Key} failed", key); }
            }

            foreach (var (key, id) in desired)
            {
                if (actual.TryGetValue(key, out var sig) && sig == _fw.Signature(id)) continue;
                try { _fw.Block(id); appliedNow.Add(key); newlyBlocked.Add(id); }
                catch (Exception ex) { errors.Add(ex.Message); _log.LogError(ex, "Block {Key} failed", key); }
            }

            bool globalTurnedOn = false;
            if (desiredGlobal != globalNow)
            {
                try { _fw.SetGlobalBlock(desiredGlobal); globalTurnedOn = desiredGlobal; globalNow = desiredGlobal; }
                catch (Exception ex) { errors.Add(ex.Message); _log.LogError(ex, "Global block failed"); }
            }

            // Rules stop new traffic; dropping open connections stops what is already flowing.
            try
            {
                if (globalTurnedOn) _killer.KillAll();
                foreach (var id in newlyBlocked)
                {
                    var pids = _engine.PidsFor(id.Key).Union(_resolver.FindPids(id)).ToList();
                    if (pids.Count > 0) _killer.Kill(pids);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Closing connections failed"); }

            try { FirewallEnabled = _fw.IsEnabled(); } catch { FirewallEnabled = false; }

            bool changed;
            lock (_stateGate)
            {
                changed = !_applied.SetEquals(appliedNow) || _global != globalNow;
                _applied = appliedNow;
                _global = globalNow;
            }
            var err = errors.Count == 0 ? null : errors[0];
            changed |= err != LastError;
            LastError = err;
            if (changed) _hub.Notify();
        }
    }

    void Fail(string message, Exception ex)
    {
        _log.LogError(ex, "{Message}", message);
        if (LastError != message) { LastError = message; _hub.Notify(); }
    }

    public void EnableFirewall()
    {
        _fw.Enable();
        Signal();
    }
}
