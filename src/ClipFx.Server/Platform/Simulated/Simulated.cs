using System.Net;
using ClipFx.Model;

namespace ClipFx.Platform.Simulated;

/// <summary>Fake platform used for development and automated tests on any OS.</summary>
public static class SimulatedPlatform
{
    public sealed record SimApp(int Pid, AppIdentity Identity, double MeanRateDown, double MeanRateUp);

    public static IReadOnlyList<SimApp> Apps { get; } = Build();

    static IReadOnlyList<SimApp> Build()
    {
        AppIdentity Prog(string name, string path, bool sys = false) =>
            new(AppIdentity.ProgramKey(path), AppKind.Program, name, path, [], null, null, sys);
        AppIdentity Svc(string name, params string[] services) =>
            new(AppIdentity.ServiceKey(services), AppKind.Service, name, @"C:\Windows\System32\svchost.exe", services, null, null, true);

        return
        [
            new(1001, Prog("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"), 180_000, 12_000),
            new(1002, Prog("Steam", @"C:\Program Files (x86)\Steam\steam.exe"), 60_000, 3_000),
            new(1003, new AppIdentity(AppIdentity.PackageKey("SpotifyAB.SpotifyMusic_zpdnekdrzrea0"), AppKind.Packaged, "Spotify",
                @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.2.0.0_x64__zpdnekdrzrea0\Spotify.exe", [],
                "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "S-1-15-2-1-2-3-4-5-6-7", false), 40_000, 2_000),
            new(1004, Prog("Microsoft Teams", @"C:\Users\User\AppData\Local\Microsoft\Teams\current\Teams.exe"), 25_000, 18_000),
            new(1005, Prog("Microsoft OneDrive", @"C:\Users\User\AppData\Local\Microsoft\OneDrive\OneDrive.exe"), 15_000, 40_000),
            new(1006, Prog("Discord", @"C:\Users\User\AppData\Local\Discord\app-1.0.9\Discord.exe"), 8_000, 2_000),
            new(1007, Prog("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"), 20_000, 2_000),
            new(2001, Svc("Windows Update", "wuauserv"), 120_000, 2_000),
            new(2002, Svc("Delivery Optimization", "DoSvc"), 90_000, 30_000),
            new(2003, Prog("Antimalware Service Executable", @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.2\MsMpEng.exe", sys: true), 6_000, 1_000),
            new(2004, Svc("DNS Client", "Dnscache"), 600, 300),
            new(4, AppIdentity.Kernel, 2_000, 800),
        ];
    }

    public static PlatformServices Create(bool autoTraffic = true)
    {
        var fw = new SimulatedFirewall();
        var resolver = new SimulatedResolver();
        var source = new SimulatedTrafficSource(fw, resolver) { AutoTraffic = autoTraffic };
        return new PlatformServices(source, resolver, fw, new NoopKiller(), new NoIcons(), Simulated: true);
    }

    sealed class NoopKiller : IConnectionKiller
    {
        public void Kill(IReadOnlyCollection<int> pids) { }
        public void KillAll() { }
    }

    sealed class NoIcons : IIconProvider
    {
        public byte[]? GetPng(AppIdentity identity) => null;
    }
}

public sealed class SimulatedResolver : IProcessResolver
{
    public AppIdentity? Resolve(int pid) =>
        SimulatedPlatform.Apps.FirstOrDefault(a => a.Pid == pid)?.Identity;
    public void Hint(int pid, string imagePath) { }
    public void Exited(int pid) { }
    public IReadOnlyList<int> FindPids(AppIdentity identity) =>
        SimulatedPlatform.Apps.Where(a => a.Identity.Key == identity.Key).Select(a => a.Pid).ToList();
}

public sealed class SimulatedFirewall : IFirewall
{
    readonly object _gate = new();
    readonly Dictionary<string, string> _rules = new(StringComparer.Ordinal);
    public bool Enabled { get; set; } = true;
    public int Writes { get; private set; }

    public IReadOnlyDictionary<string, string> GetApplied() { lock (_gate) return new Dictionary<string, string>(_rules); }
    public string Signature(AppIdentity identity) => identity.Path ?? identity.Key;

    public void Block(AppIdentity identity)
    {
        if (!identity.CanBlock) throw new InvalidOperationException("Can't block " + identity.Name);
        lock (_gate) { _rules[identity.Key] = Signature(identity); Writes++; }
    }

    public void Unblock(string key) { lock (_gate) { _rules.Remove(key); Writes++; } }

    public void SetGlobalBlock(bool on)
    {
        lock (_gate) { if (on) _rules[IFirewall.GlobalKey] = "global"; else _rules.Remove(IFirewall.GlobalKey); Writes++; }
    }

    public bool IsEnabled() => Enabled;
    public void Enable() => Enabled = true;
    public void RemoveAll() { lock (_gate) _rules.Clear(); }

    /// <summary>Would traffic from this identity pass? Enforcement only counts when the firewall is on.</summary>
    public bool Allows(AppIdentity identity)
    {
        lock (_gate)
        {
            if (!Enabled) return true;
            return !_rules.ContainsKey(IFirewall.GlobalKey) && !_rules.ContainsKey(identity.Key);
        }
    }
}

public sealed class SimulatedTrafficSource : ITrafficSource
{
    readonly SimulatedFirewall _fw;
    readonly SimulatedResolver _resolver;
    readonly Random _rng = new(7);
    Timer? _timer;
    static readonly IPAddress Remote = IPAddress.Parse("142.250.72.14");

    public SimulatedTrafficSource(SimulatedFirewall fw, SimulatedResolver resolver)
    {
        _fw = fw;
        _resolver = resolver;
    }

    public event Action<TrafficEvent>? Traffic;
    public event Action<int, string>? ProcessStarted { add { } remove { } }
    public event Action<int>? ProcessStopped { add { } remove { } }

    public bool AutoTraffic { get; set; } = true;
    public bool IsRunning => _timer != null;
    public string? Error => null;

    public void Start() => _timer = new Timer(_ => Generate(), null, 200, 200);

    /// <summary>Emits traffic for a pid, unless the firewall would stop it.</summary>
    public bool Inject(int pid, long bytes, bool sent)
    {
        var id = _resolver.Resolve(pid);
        if (id != null && !_fw.Allows(id)) return false;
        Traffic?.Invoke(new TrafficEvent(pid, bytes, sent, Remote));
        return true;
    }

    void Generate()
    {
        if (!AutoTraffic) return;
        double t = Environment.TickCount64 / 1000.0;
        foreach (var app in SimulatedPlatform.Apps)
        {
            double wave;
            lock (_rng) wave = 0.25 + Math.Abs(Math.Sin(t / (3 + app.Pid % 7) + app.Pid)) * 1.5 + _rng.NextDouble() * 0.4;
            bool idle = Math.Sin(t / (11 + app.Pid % 5) + app.Pid * 3) < -0.35;
            if (idle) continue;
            Inject(app.Pid, (long)(app.MeanRateDown * 0.2 * wave), false);
            Inject(app.Pid, (long)(app.MeanRateUp * 0.2 * wave), true);
        }
    }

    public void Dispose() { _timer?.Dispose(); _timer = null; }
}
