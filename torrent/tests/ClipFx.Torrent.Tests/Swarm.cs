using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.TrackerServer;
using Xunit.Abstractions;

namespace ClipFx.Torrents.Tests;

/// <summary>A tiny private internet on loopback: one UDP tracker plus seeders.</summary>
sealed class Swarm : IAsyncDisposable
{
    public readonly string Root;
    public readonly int TrackerPort;
    public string TrackerUrl => $"udp://127.0.0.1:{TrackerPort}/announce";
    readonly MonoTorrent.TrackerServer.TrackerServer _tracker;
    readonly ITrackerListener _listener;
    readonly List<ClientEngine> _seeders = new();
    readonly List<TorrentClient> _clients = new();

    public Swarm()
    {
        Root = Path.Combine(Path.GetTempPath(), "cft-tests", Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(Root);
        TrackerPort = FreePort(udp: true);
        _tracker = new MonoTorrent.TrackerServer.TrackerServer { AllowUnregisteredTorrents = true, AnnounceInterval = TimeSpan.FromSeconds(5), MinAnnounceInterval = TimeSpan.FromSeconds(1) };
        _listener = TrackerListenerFactory.CreateUdp(IPAddress.Loopback, TrackerPort);
        _tracker.RegisterListener(_listener);
        _listener.Start();
    }

    public static int FreePort(bool udp = false)
    {
        if (udp)
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        }
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>Creates random files and a .torrent announcing to our tracker.</summary>
    public async Task<(string Dir, string TorrentPath, Torrent Torrent)> MakeContentAsync(string name, params long[] sizes)
    {
        var dir = Path.Combine(Root, "src-" + name);
        var content = Path.Combine(dir, name);
        Directory.CreateDirectory(content);
        var rnd = new Random(name.GetHashCode());
        var buf = new byte[1 << 20];
        for (int i = 0; i < sizes.Length; i++)
        {
            var sub = i == 1 ? Path.Combine(content, "extras") : content;
            Directory.CreateDirectory(sub);
            using var fs = File.Create(Path.Combine(sub, $"part{i}.bin"));
            for (long w = 0; w < sizes[i]; w += buf.Length)
            {
                rnd.NextBytes(buf);
                fs.Write(buf, 0, (int)Math.Min(buf.Length, sizes[i] - w));
            }
        }
        var creator = new TorrentCreator { PieceLength = 1 << 20 };
        creator.Announces.Add(new List<string> { TrackerUrl });
        var dict = await creator.CreateAsync(new TorrentFileSource(content));
        var path = Path.Combine(Root, name + ".torrent");
        await File.WriteAllBytesAsync(path, dict.Encode());
        return (dir, path, Torrent.Load(path));
    }

    public async Task<ClientEngine> SeedAsync(Torrent t, string dir, int uploadLimit = 0)
    {
        int port = FreePort();
        var engine = new ClientEngine(new EngineSettingsBuilder
        {
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            DhtEndPoint = null,
            ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, port) },
            CacheDirectory = Path.Combine(Root, "seedcache-" + Guid.NewGuid().ToString("n")[..6]),
            MaximumUploadRate = uploadLimit,
        }.ToSettings());
        var m = await engine.AddAsync(t, dir, new TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false, UploadSlots = 20 }.ToSettings());
        await m.HashCheckAsync(true);
        if (!m.Complete) throw new InvalidOperationException("seed data incomplete");
        lock (_seeders) _seeders.Add(engine);
        return engine;
    }

    public async Task StopSeederAsync(ClientEngine e)
    {
        lock (_seeders) _seeders.Remove(e);
        try { await e.StopAllAsync(TimeSpan.FromSeconds(3)); } catch { }
        e.Dispose();
    }

    /// <summary>The app's engine with internet-facing extras switched off so tests stay local.</summary>
    public async Task<TorrentClient> ClientAsync(string name = "client", Action<Settings>? tweak = null)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        var settingsPath = Path.Combine(dir, "settings.json");
        if (!File.Exists(settingsPath))
        {
            var s = new Settings
            {
                SaveDir = Path.Combine(Root, name + "-downloads"),
                Port = FreePort(),
                PortForwarding = false, Dht = false, LocalPeers = false, ExtraTrackers = false, KeepAwake = false,
                Seeding = "forever",
            };
            tweak?.Invoke(s);
            File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(s));
        }
        var c = new TorrentClient(dir) { LonelyAfter = TimeSpan.FromSeconds(2), ReannounceEvery = TimeSpan.FromSeconds(3) };
        await c.StartAsync();
        lock (_clients) _clients.Add(c);
        return c;
    }

    public async Task CloseAsync(TorrentClient c)
    {
        lock (_clients) _clients.Remove(c);
        await c.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients.ToList()) try { await c.DisposeAsync(); } catch { }
        foreach (var s in _seeders.ToList()) try { await StopSeederAsync(s); } catch { }
        try { _listener.Stop(); } catch { }
        _tracker.Dispose();
        for (int i = 0; i < 5; i++)
        {
            try { Directory.Delete(Root, true); break; } catch { await Task.Delay(300); }
        }
    }

    public static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    public static async Task<TorrentView> WaitAsync(TorrentClient c, string id, Func<TorrentView, bool> cond, TimeSpan timeout, ITestOutputHelper? log = null)
    {
        var end = DateTime.UtcNow + timeout;
        TorrentView? v = null;
        var lastLog = DateTime.MinValue;
        while (DateTime.UtcNow < end)
        {
            v = c.Torrents().FirstOrDefault(t => t.Id == id);
            if (v != null && cond(v)) return v;
            if (log != null && v != null && DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(2))
            {
                lastLog = DateTime.UtcNow;
                log.WriteLine($"  {v.Status} {v.Progress:P1} {v.Down / 1e6:F1} MB/s peers {v.Peers} {v.Error}");
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out. Last: {v?.Status} {v?.Progress:P1} peers={v?.Peers} err={v?.Error}");
    }
}
