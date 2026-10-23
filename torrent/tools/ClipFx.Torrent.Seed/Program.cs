using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections.TrackerServer;

// Test helper: a local tracker and seeders for a folder of random files.
// usage: Seed <workDir> <sizeMB> [seeders]
// Prints "TORRENT <path>" and "MAGNET <link>" then seeds until killed.
var work = Path.GetFullPath(args[0]);
long size = long.Parse(args[1]) << 20;
int seeders = args.Length > 2 ? int.Parse(args[2]) : 3;
var content = Path.Combine(work, "seed", "Test Pack");
Directory.CreateDirectory(Path.Combine(content, "extras"));
var rnd = new Random(7);
var buf = new byte[1 << 20];
void Make(string path, long len)
{
    using var fs = File.Create(path);
    for (long w = 0; w < len; w += buf.Length) { rnd.NextBytes(buf); fs.Write(buf, 0, (int)Math.Min(buf.Length, len - w)); }
}
Make(Path.Combine(content, "movie.mkv"), size);
Make(Path.Combine(content, "extras", "notes.txt"), 12_345);
Make(Path.Combine(content, "extras", "cover.jpg"), 2_000_000);

int tport = 47790;
var tracker = new MonoTorrent.TrackerServer.TrackerServer { AllowUnregisteredTorrents = true, AnnounceInterval = TimeSpan.FromSeconds(10), MinAnnounceInterval = TimeSpan.FromSeconds(2) };
var listener = TrackerListenerFactory.CreateUdp(IPAddress.Loopback, tport);
tracker.RegisterListener(listener);
listener.Start();

var creator = new TorrentCreator { PieceLength = 2 << 20 };
creator.Announces.Add(new List<string> { $"udp://127.0.0.1:{tport}/announce" });
var dict = await creator.CreateAsync(new TorrentFileSource(content));
var torrent = Torrent.Load(dict);
var tpath = Path.Combine(work, "Test Pack.torrent");
File.WriteAllBytes(tpath, dict.Encode());

var engines = new List<ClientEngine>();
for (int i = 0; i < seeders; i++)
{
    var e = new ClientEngine(new EngineSettingsBuilder
    {
        AllowPortForwarding = false, AllowLocalPeerDiscovery = false, DhtEndPoint = null,
        ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, 47800 + i) },
        CacheDirectory = Path.Combine(work, "cache" + i),
    }.ToSettings());
    var m = await e.AddAsync(torrent, Path.Combine(work, "seed"), new TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false, UploadSlots = 20 }.ToSettings());
    await m.HashCheckAsync(true);
    engines.Add(e);
}
Console.WriteLine("TORRENT " + tpath);
Console.WriteLine("MAGNET " + new MagnetLink(torrent.InfoHashes, torrent.Name, new List<string> { $"udp://127.0.0.1:{tport}/announce" }, null, torrent.Size).ToV1String());
Console.WriteLine("HASH " + torrent.InfoHashes.V1OrV2.ToHex().ToLowerInvariant());
Console.WriteLine("READY");
await Task.Delay(Timeout.Infinite);
