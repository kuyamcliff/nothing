using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;

namespace ClipFx.Torrents;

/// <summary>
/// The engine's DHT, started with several well-known bootstrap routers instead of just one,
/// so it can join the network even when one router is down or unreachable.
/// </summary>
sealed class BootstrapDht(IDhtEngine inner) : IDhtEngine
{
    internal static readonly (string Host, int Port)[] Routers =
    [
        ("router.bittorrent.com", 6881),
        ("dht.transmissionbt.com", 6881),
        ("router.utorrent.com", 6881),
        ("dht.libtorrent.org", 25401),
        ("router.bitcomet.com", 6881),
        ("dht.aelitis.com", 6881),
    ];

    /// <summary>Compact node entries (20-byte id, IPv4, port) for every router we can resolve.</summary>
    internal static async Task<byte[]> RouterNodesAsync()
    {
        var lookups = Routers.Select(async r =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var addrs = await Dns.GetHostAddressesAsync(r.Host, AddressFamily.InterNetwork, cts.Token);
                return addrs.Take(4).Select(a => new IPEndPoint(a, r.Port));
            }
            catch { return []; }
        });
        return Encode((await Task.WhenAll(lookups)).SelectMany(x => x).Distinct().ToList());
    }

    static byte[] Encode(List<IPEndPoint> eps)
    {
        var buf = new byte[eps.Count * 26];
        for (int i = 0; i < eps.Count; i++)
        {
            var span = buf.AsSpan(i * 26, 26);
            Random.Shared.NextBytes(span[..20]);
            eps[i].Address.GetAddressBytes().CopyTo(span[20..24]);
            span[24] = (byte)(eps[i].Port >> 8);
            span[25] = (byte)eps[i].Port;
        }
        return buf;
    }

    public Task StartAsync() => StartAsync(ReadOnlyMemory<byte>.Empty);

    public async Task StartAsync(ReadOnlyMemory<byte> initialNodes)
    {
        // Saved nodes from last time go first; routers are added so a stale cache can't leave us stranded.
        var routers = await RouterNodesAsync();
        var all = new byte[initialNodes.Length + routers.Length];
        initialNodes.CopyTo(all);
        routers.CopyTo(all, initialNodes.Length);
        await inner.StartAsync(all);
    }

    public TimeSpan AnnounceInterval => inner.AnnounceInterval;
    public bool Disposed => inner.Disposed;
    public ITransferMonitor Monitor => inner.Monitor;
    public TimeSpan MinimumAnnounceInterval => inner.MinimumAnnounceInterval;
    public int NodeCount => inner.NodeCount;
    public DhtState State => inner.State;

    public event EventHandler<PeersFoundEventArgs> PeersFound { add => inner.PeersFound += value; remove => inner.PeersFound -= value; }
    public event EventHandler StateChanged { add => inner.StateChanged += value; remove => inner.StateChanged -= value; }

    public void Add(IEnumerable<ReadOnlyMemory<byte>> nodes) => inner.Add(nodes);
    public void Announce(InfoHash infoHash, int port) => inner.Announce(infoHash, port);
    public void GetPeers(InfoHash infoHash) => inner.GetPeers(infoHash);
    public Task<ReadOnlyMemory<byte>> SaveNodesAsync() => inner.SaveNodesAsync();
    public Task SetListenerAsync(IDhtListener listener) => inner.SetListenerAsync(listener);
    public Task StopAsync() => inner.StopAsync();
    public void Dispose() => inner.Dispose();
}
