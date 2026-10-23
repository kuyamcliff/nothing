using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ClipFx.Share;

/// <summary>Finds devices with UDP multicast + broadcast, with a TCP subnet scan as fallback.</summary>
public sealed class Discovery : IDisposable
{
    readonly Func<DeviceInfo> _self;
    readonly Func<bool> _discoverable;
    readonly int _udpPort;
    UdpClient? _udp;
    CancellationTokenSource? _cts;
    readonly IPAddress _group = IPAddress.Parse(Proto.MulticastGroup);

    public event Action<DeviceInfo, IPAddress>? Seen;

    public Discovery(Func<DeviceInfo> self, Func<bool> discoverable, int udpPort = Proto.UdpPort)
    {
        _self = self;
        _discoverable = discoverable;
        _udpPort = udpPort;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _udpPort));
            _udp.EnableBroadcast = true;
            _udp.MulticastLoopback = false;
            JoinGroups();
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch { /* UDP unavailable: manual add and scans still work */ }
        _ = Task.Run(() => AnnounceLoop(_cts.Token));
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    void OnNetworkChanged(object? s, EventArgs e)
    {
        JoinGroups();
        _ = Task.Run(() => Announce(query: true));
    }

    void JoinGroups()
    {
        if (_udp is null) return;
        foreach (var addr in LocalIPv4())
            try { _udp.JoinMulticastGroup(_group, addr.Address); } catch { }
    }

    public static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }
            foreach (var u in props.UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                    yield return (u.Address, u.IPv4Mask ?? IPAddress.Parse("255.255.255.0"));
        }
    }

    byte[] Packet(string type) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["cfs"] = Proto.Version, ["t"] = type, ["device"] = Json.From(_self()),
    }.ToJsonString());

    public void Announce(bool query)
    {
        if (_udp is null) return;
        if (!_discoverable() && !query) return;
        var data = Packet(query ? "query" : "announce");
        var targets = new HashSet<IPAddress> { _group, IPAddress.Broadcast };
        foreach (var (a, m) in LocalIPv4())
        {
            var ab = a.GetAddressBytes(); var mb = m.GetAddressBytes();
            var bc = new byte[4];
            for (int i = 0; i < 4; i++) bc[i] = (byte)(ab[i] | ~mb[i]);
            targets.Add(new IPAddress(bc));
        }
        foreach (var t in targets)
            try { _udp.Send(data, data.Length, new IPEndPoint(t, _udpPort)); } catch { }
    }

    async Task AnnounceLoop(CancellationToken ct)
    {
        Announce(query: true);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch { return; }
            Announce(query: false);
        }
    }

    async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { continue; }
            try
            {
                if (JsonNode.Parse(r.Buffer) is not JsonObject o || o.Long("cfs") < 1) continue;
                var dev = Json.To<DeviceInfo>(o["device"]);
                if (dev is null || dev.Id == _self().Id || string.IsNullOrEmpty(dev.Id)) continue;
                Seen?.Invoke(dev, r.RemoteEndPoint.Address);
                if (o.Str("t") == "query" && _discoverable())
                {
                    var reply = Packet("announce");
                    await _udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                }
            }
            catch { }
        }
    }

    /// <summary>Asks one address who it is.</summary>
    public static async Task<DeviceInfo?> HelloAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct, Func<Socket, Task>? prepare = null)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await using var c = await Conn.ConnectAsync(address, port, timeout, cts.Token, prepare);
            await c.WriteMessageAsync(new JsonObject { ["op"] = "hello" }, cts.Token);
            var r = await c.ReadMessageAsync(cts.Token, timeout);
            return r != null && r.Bool("ok") ? Json.To<DeviceInfo>(r["device"]) : null;
        }
        catch { return null; }
    }

    /// <summary>Tries every address on the local /24 networks. For networks that block UDP.</summary>
    public async Task ScanAsync(int port, CancellationToken ct)
    {
        var self = _self();
        var targets = new List<IPAddress>();
        foreach (var (a, _) in LocalIPv4())
        {
            var b = a.GetAddressBytes();
            for (int h = 1; h < 255; h++)
                if (h != b[3]) targets.Add(new IPAddress([b[0], b[1], b[2], (byte)h]));
        }
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 96, CancellationToken = ct }, async (ip, token) =>
        {
            var dev = await HelloAsync(ip, port, TimeSpan.FromMilliseconds(700), token);
            if (dev != null && dev.Id != self.Id) Seen?.Invoke(dev, ip);
        });
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _cts?.Cancel();
        _udp?.Dispose();
    }
}
