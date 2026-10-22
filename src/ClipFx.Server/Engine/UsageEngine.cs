using ClipFx.Model;

namespace ClipFx.Engine;

/// <summary>
/// Thread-safe accounting of data usage per app, limits and user policy.
/// Traffic arrives from the monitor thread; commands arrive from the API.
/// </summary>
public sealed class UsageEngine
{
    public const int HistoryLength = 60;
    static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PidLifetime = TimeSpan.FromMinutes(2);

    sealed class Live
    {
        public long WinUp, WinDown;
        public double Up, Down;
        public DateTime LastActivity;
        public readonly Dictionary<int, DateTime> Pids = new();
    }

    readonly object _gate = new();
    readonly PersistedState _s;
    readonly TimeProvider _time;
    readonly Dictionary<string, string> _idToKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);
    readonly Queue<RatePoint> _history = new();
    long _ovWinUp, _ovWinDown;
    double _ovUp, _ovDown;
    long _lastTickTicks;
    long _version;
    bool _dirty;

    /// <summary>Raised when what should be blocked may have changed.</summary>
    public event Action? PolicyChanged;
    /// <summary>Raised when anything visible changed.</summary>
    public event Action? Changed;

    public UsageEngine(PersistedState state, TimeProvider? time = null)
    {
        _s = state;
        _time = time ?? TimeProvider.System;
        foreach (var key in _s.Apps.Keys) _idToKey[KeyId.Of(key)] = key;
        if (_s.PeriodStart == default)
        {
            var now = LocalNow;
            _s.PeriodStart = Period.CurrentStart(now, _s.Settings) ?? now;
            _dirty = true;
        }
        _lastTickTicks = _time.GetTimestamp();
        for (int i = 0; i < HistoryLength; i++) _history.Enqueue(new RatePoint(0, 0));
    }

    DateTime LocalNow => _time.GetLocalNow().DateTime;
    DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public long Version { get { lock (_gate) return _version; } }
    public bool IsDirty { get { lock (_gate) return _dirty; } }

    // ---------------------------------------------------------------- traffic

    public void Record(AppIdentity app, int pid, long bytes, bool sent)
    {
        if (bytes <= 0) return;
        bool policy = false, isNew;
        lock (_gate)
        {
            var now = UtcNow;
            isNew = !_s.Apps.ContainsKey(app.Key);
            var rec = GetOrAdd(app, now);
            var live = LiveOf(app.Key);
            bool wasReached = rec.LimitReached, ovWas = _s.OverallLimitReached;

            if (sent) { rec.Sent += bytes; _s.OverallSent += bytes; live.WinUp += bytes; _ovWinUp += bytes; }
            else { rec.Received += bytes; _s.OverallReceived += bytes; live.WinDown += bytes; _ovWinDown += bytes; }

            rec.LastSeen = now;
            live.LastActivity = now;
            if (pid > 0) live.Pids[pid] = now;

            if (!wasReached && rec.LimitReached) policy = true;
            if (!ovWas && _s.OverallLimitReached) policy = true;
            _dirty = true;
            if (policy || isNew) _version++;
        }
        if (policy) PolicyChanged?.Invoke();
        if (policy || isNew) Changed?.Invoke();
    }

    AppRecord GetOrAdd(AppIdentity app, DateTime now)
    {
        if (_s.Apps.TryGetValue(app.Key, out var rec))
        {
            // Packaged apps move on update; keep the newest path/name.
            if (rec.Identity != app && app.Kind != AppKind.Unknown) rec.Identity = app;
            return rec;
        }
        rec = new AppRecord { Identity = app, FirstSeen = now, LastSeen = now };
        _s.Apps[app.Key] = rec;
        _idToKey[app.Id] = app.Key;
        return rec;
    }

    Live LiveOf(string key)
    {
        if (!_live.TryGetValue(key, out var l)) _live[key] = l = new Live();
        return l;
    }

    /// <summary>Call about once per second: computes live rates and applies period resets.</summary>
    public void Tick()
    {
        bool policy = false;
        lock (_gate)
        {
            long ts = _time.GetTimestamp();
            double secs = Math.Max(0.05, _time.GetElapsedTime(_lastTickTicks, ts).TotalSeconds);
            _lastTickTicks = ts;
            var now = UtcNow;

            foreach (var (_, l) in _live)
            {
                l.Up = l.WinUp / secs; l.Down = l.WinDown / secs;
                l.WinUp = l.WinDown = 0;
                if (l.Pids.Count > 0)
                    foreach (var pid in l.Pids.Where(p => now - p.Value > PidLifetime).Select(p => p.Key).ToList())
                        l.Pids.Remove(pid);
            }
            _ovUp = _ovWinUp / secs; _ovDown = _ovWinDown / secs;
            _ovWinUp = _ovWinDown = 0;
            _history.Enqueue(new RatePoint(_ovUp, _ovDown));
            while (_history.Count > HistoryLength) _history.Dequeue();

            var start = Period.CurrentStart(LocalNow, _s.Settings);
            if (start is DateTime st && st > _s.PeriodStart)
            {
                policy = AnyLimitReached();
                ClearCounters();
                _s.PeriodStart = st;
                _dirty = true;
            }
            _version++;
        }
        if (policy) PolicyChanged?.Invoke();
        Changed?.Invoke();
    }

    bool AnyLimitReached() => _s.OverallLimitReached || _s.Apps.Values.Any(a => a.LimitReached);

    void ClearCounters()
    {
        foreach (var a in _s.Apps.Values) a.Sent = a.Received = 0;
        _s.OverallSent = _s.OverallReceived = 0;
    }

    // --------------------------------------------------------------- commands

    public bool SetBlocked(string id, bool blocked) => Mutate(id, r => r.Blocked = blocked, policy: true);
    public bool SetLimit(string id, long? limit) => Mutate(id, r => r.Limit = limit is > 0 ? limit : null, policy: true);
    public bool ResetApp(string id) => Mutate(id, r => r.Sent = r.Received = 0, policy: true);

    public enum ForgetResult { Ok, NotFound, Blocked }

    public ForgetResult Forget(string id)
    {
        lock (_gate)
        {
            if (!_idToKey.TryGetValue(id, out var key) || !_s.Apps.TryGetValue(key, out var rec)) return ForgetResult.NotFound;
            if (rec.EffectiveBlocked) return ForgetResult.Blocked;
            _s.Apps.Remove(key);
            _idToKey.Remove(id);
            _live.Remove(key);
            _dirty = true; _version++;
        }
        Changed?.Invoke();
        return ForgetResult.Ok;
    }

    public void SetOverallLimit(long? limit)
    {
        lock (_gate) { _s.Settings.OverallLimit = limit is > 0 ? limit : null; _dirty = true; _version++; }
        PolicyChanged?.Invoke(); Changed?.Invoke();
    }

    public void ResetUsage()
    {
        lock (_gate) { ClearCounters(); _s.PeriodStart = LocalNow; _dirty = true; _version++; }
        PolicyChanged?.Invoke(); Changed?.Invoke();
    }

    public void UpdateSettings(string? theme, string? resetPeriod, int? resetDay, bool? showSystem)
    {
        lock (_gate)
        {
            var s = _s.Settings;
            if (theme is "system" or "light" or "dark") s.Theme = theme;
            if (resetPeriod != null && Period.IsValid(resetPeriod)) s.ResetPeriod = resetPeriod;
            if (resetDay is int d) s.ResetDay = Math.Clamp(d, 1, 28);
            if (showSystem is bool b) s.ShowSystem = b;
            // A new schedule should not wipe usage on the spot; it applies from the next boundary.
            if (Period.CurrentStart(LocalNow, s) is DateTime st && st > _s.PeriodStart) _s.PeriodStart = st;
            _dirty = true; _version++;
        }
        Changed?.Invoke();
    }

    bool Mutate(string id, Action<AppRecord> change, bool policy)
    {
        lock (_gate)
        {
            if (!_idToKey.TryGetValue(id, out var key) || !_s.Apps.TryGetValue(key, out var rec)) return false;
            change(rec);
            _dirty = true; _version++;
        }
        if (policy) PolicyChanged?.Invoke();
        Changed?.Invoke();
        return true;
    }

    // ------------------------------------------------------------------ query

    public string? KeyOf(string id) { lock (_gate) return _idToKey.GetValueOrDefault(id); }

    public AppIdentity? IdentityOf(string id)
    {
        lock (_gate)
            return _idToKey.TryGetValue(id, out var key) && _s.Apps.TryGetValue(key, out var r) ? r.Identity : null;
    }

    public (IReadOnlyList<AppIdentity> Blocked, bool Global) Desired()
    {
        lock (_gate)
        {
            var list = _s.Apps.Values.Where(a => a.EffectiveBlocked && a.Identity.CanBlock).Select(a => a.Identity).ToList();
            return (list, _s.OverallLimitReached);
        }
    }

    public IReadOnlyList<int> PidsFor(string key)
    {
        lock (_gate) return _live.TryGetValue(key, out var l) ? l.Pids.Keys.ToList() : [];
    }

    public Snapshot GetSnapshot(IEnforcementState enforcement, StatusView status)
    {
        lock (_gate)
        {
            var now = UtcNow;
            var apps = new List<AppView>(_s.Apps.Count);
            foreach (var (key, r) in _s.Apps)
            {
                _live.TryGetValue(key, out var l);
                var id = r.Identity;
                bool applied = !id.CanBlock || enforcement.IsApplied(key) == r.EffectiveBlocked;
                apps.Add(new AppView(
                    id.Id, id.Name, id.Path, id.Kind, id.IsSystem, id.CanBlock,
                    r.Sent, r.Received, r.Total, l?.Up ?? 0, l?.Down ?? 0,
                    r.Blocked, r.Limit, r.LimitReached, r.EffectiveBlocked, applied,
                    l != null && now - l.LastActivity < ActiveWindow, r.FirstSeen, r.LastSeen));
            }
            var s = _s.Settings;
            var local = LocalNow;
            return new Snapshot(
                _version, now, status,
                new SettingsView(s.Theme, s.ResetPeriod, s.ResetDay, s.ShowSystem),
                new PeriodView(_s.PeriodStart, Period.NextReset(local, s), s.ResetPeriod, s.ResetDay),
                new OverallView(_s.OverallSent, _s.OverallReceived, _s.OverallTotal, _ovUp, _ovDown,
                    s.OverallLimit, _s.OverallLimitReached, enforcement.GlobalApplied == _s.OverallLimitReached,
                    _history.ToList()),
                apps);
        }
    }

    /// <summary>Serializes state for saving and clears the dirty flag.</summary>
    public byte[] Serialize()
    {
        lock (_gate)
        {
            var bytes = StateStore.Serialize(_s);
            _dirty = false;
            return bytes;
        }
    }
}
