using System.Net;
using ClipFx.Torrents;
using ClipFx.Torrents.Web;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections.TrackerServer;
using Microsoft.Extensions.Hosting;

// Runs the real engine + UI in a browser for development and screenshots.
// usage: DevHost <dataDir> <uiPort> [--ui <wwwroot>] [--demo]
var dataDir = args[0];
int port = int.Parse(args[1]);
string? ui = args.SkipWhile(a => a != "--ui").Skip(1).FirstOrDefault();
bool demo = args.Contains("--demo");

var client = new TorrentClient(dataDir);
if (demo)
{
    await client.UpdateSettingsAsync(s => { s.PortForwarding = false; s.Dht = false; s.LocalPeers = false; s.ExtraTrackers = false; s.SaveDir = Path.Combine(dataDir, "Downloads"); });
}
await client.StartAsync();
var app = UiServer.Build(client, new DevActions(), port, ui);
await app.StartAsync();
Console.WriteLine("UI " + UiServer.Address(app));

if (demo) await Demo.RunAsync(client, dataDir);
await app.WaitForShutdownAsync();
await client.DisposeAsync();

sealed class DevActions : IHostActions
{
    string? _clip;
    public void CopyText(string text) => _clip = text;
    public string? ReadClipboard() => _clip;
    public Task<string?> PickFolderAsync(string? title = null) => Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), "picked"));
}

static class Demo
{
    public static async Task RunAsync(TorrentClient client, string dataDir)
    {
        if (client.Torrents().Count > 0) return;
        var root = Path.Combine(dataDir, "demo-seed");
        int tport = 47700;
        var tracker = new MonoTorrent.TrackerServer.TrackerServer { AllowUnregisteredTorrents = true };
        var l = TrackerListenerFactory.CreateUdp(IPAddress.Loopback, tport);
        tracker.RegisterListener(l); l.Start();
        var items = new (string Name, string[] Files, long[] Sizes, int Rate, int Seeders)[]
        {
            ("Ubuntu 24.04.1 Desktop amd64", ["ubuntu-24.04.1-desktop-amd64.iso"], [900L << 20], 9 << 20, 3),
            ("Blender Open Movie - Sintel 4K", ["Sintel.2010.4k.mkv", "Sintel.2010.1080p.mkv", "subs/sintel_en.srt", "subs/sintel_de.srt", "poster.jpg"], [700L << 20, 300L << 20, 40_000, 41_000, 900_000], 5 << 20, 2),
            ("Debian 12.7 netinst", ["debian-12.7.0-amd64-netinst.iso"], [120L << 20], 30 << 20, 2),
            ("Creative Commons Music Pack", ["01 - Opening.flac", "02 - Night Drive.flac", "03 - Coastline.flac", "cover.png"], [60L << 20, 70L << 20, 55L << 20, 2_000_000], 2 << 20, 1),
            ("LibreOffice 24.8 Win x64", ["LibreOffice_24.8_Win_x86-64.msi"], [330L << 20], 0, 0),
        };
        int i = 0;
        foreach (var it in items)
        {
            var dir = Path.Combine(root, it.Name);
            var rnd = new Random(i);
            var buf = new byte[1 << 20];
            for (int f = 0; f < it.Files.Length; f++)
            {
                var p = Path.Combine(dir, it.Files[f]);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                using var fs = File.Create(p);
                for (long w = 0; w < it.Sizes[f]; w += buf.Length) { rnd.NextBytes(buf); fs.Write(buf, 0, (int)Math.Min(buf.Length, it.Sizes[f] - w)); }
            }
            var c = new TorrentCreator { PieceLength = 1 << 20, Comment = "Demo" };
            c.Announces.Add(new List<string> { $"udp://127.0.0.1:{tport}/announce" });
            var src = it.Files.Length == 1 ? Path.Combine(dir, it.Files[0]) : dir;
            var dict = await c.CreateAsync(new TorrentFileSource(src));
            var t = Torrent.Load(dict);
            var seedDir = it.Files.Length == 1 ? dir : root;
            for (int s = 0; s < it.Seeders; s++)
            {
                var e = new ClientEngine(new EngineSettingsBuilder
                {
                    AllowPortForwarding = false, AllowLocalPeerDiscovery = false, DhtEndPoint = null,
                    ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, 47710 + i * 10 + s) },
                    CacheDirectory = Path.Combine(root, $".c{i}{s}"), MaximumUploadRate = it.Rate / Math.Max(1, it.Seeders),
                }.ToSettings());
                var m = await e.AddAsync(t, seedDir);
                await m.HashCheckAsync(true);
            }
            var tf = Path.Combine(root, $"{i}.torrent");
            File.WriteAllBytes(tf, dict.Encode());
            var r = await client.AddAsync(new AddRequest(tf, null, null, null, i == 1, null));
            if (i == 3) { await Task.Delay(4000); await client.PauseAsync(r.Id); }
            i++;
        }
    }
}
