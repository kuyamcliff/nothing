using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace ClipFx.Share;

/// <summary>Optional platform integration.</summary>
public interface IShareHost
{
    void IncomingRequest(TransferView t) { }
    void TransferFinished(TransferView t) { }
    void KeepAwake(bool active) { }
    Func<Socket, Task>? PrepareSocket => null;
}

/// <summary>Everything ClipFx Share does on one device: discovery, receiving, sending, history.</summary>
public sealed class ShareNode : IAsyncDisposable
{
    public string Platform { get; }
    readonly string _dataDir;
    readonly IShareHost _host;
    readonly object _gate = new();
    readonly ConcurrentDictionary<string, IncomingSession> _in = new();
    readonly ConcurrentDictionary<string, OutgoingSession> _out = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<(bool Accept, bool Trust)>> _decisions = new();
    readonly ConcurrentDictionary<string, (DeviceInfo Dev, IPAddress Addr, DateTime Seen, bool Manual)> _peers = new();
    readonly ConcurrentDictionary<string, DateTime> _dirty = new();
    readonly CancellationTokenSource _cts = new();
    ShareSettings _settings;
    TcpListener? _listener;
    Discovery? _discovery;
    long _version;
    bool _awake;

    public int Port { get; private set; }
    public event Action? Changed;
    public long Version => Interlocked.Read(ref _version);
    public static readonly TimeSpan DecisionTimeout = TimeSpan.FromMinutes(2);
    static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(12);

    public ShareNode(string dataDir, string platform, IShareHost? host = null)
    {
        _dataDir = dataDir;
        Platform = platform;
        _host = host ?? new NullHost();
        Directory.CreateDirectory(dataDir);
        _settings = Store.Load<ShareSettings>(SettingsPath) ?? new ShareSettings();
        Store.Save(SettingsPath, _settings);
    }

    sealed class NullHost : IShareHost { }

    string SettingsPath => Path.Combine(_dataDir, "settings.json");
    string SessionsDir => Path.Combine(_dataDir, "transfers");
    string InPath(string id) => Path.Combine(SessionsDir, $"in-{id}.json");
    string OutPath(string id) => Path.Combine(SessionsDir, $"out-{id}.json");

    public ShareSettings Settings { get { lock (_gate) return _settings.Clone(); } }

    public void UpdateSettings(Action<ShareSettings> change)
    {
        lock (_gate)
        {
            var s = _settings.Clone();
            change(s);
            s.Streams = Math.Clamp(s.Streams, 1, 16);
            s.ChunkMB = Math.Clamp(s.ChunkMB, 1, 64);
            if (s.Conflict is not ("rename" or "overwrite" or "skip")) s.Conflict = "rename";
            if (string.IsNullOrWhiteSpace(s.DeviceName)) s.DeviceName = Environment.MachineName;
            s.DeviceName = s.DeviceName.Trim()[..Math.Min(s.DeviceName.Trim().Length, 40)];
            _settings = s;
            Store.Save(SettingsPath, s);
        }
        _discovery?.Announce(query: false);
        Bump();
    }

    public DeviceInfo Self { get { var s = Settings; return new DeviceInfo(s.DeviceId, s.DeviceName, Platform, Port); } }

    void Bump()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
        UpdateAwake();
    }

    void UpdateAwake()
    {
        bool active = _in.Values.Any(s => s.State == TransferState.Active) || _out.Values.Any(s => s.State is TransferState.Active or TransferState.Connecting or TransferState.Reconnecting);
        if (active != _awake) { _awake = active; try { _host.KeepAwake(active); } catch { } }
    }

    // ------------------------------------------------------------------ start / stop

    public async Task StartAsync(bool discovery = true)
    {
        // Disk and socket work runs on pool threads; start with enough of them so the
        // first transfer isn't throttled while the pool slowly grows.
        ThreadPool.GetMinThreads(out int w, out int io);
        ThreadPool.SetMinThreads(Math.Max(w, 64), Math.Max(io, 64));
        LoadSessions();
        var preferred = Settings.Port;
        foreach (var port in new[] { preferred, 0 })
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, !OperatingSystem.IsWindows());
                _listener.Start(256);
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                break;
            }
            catch (SocketException) { _listener = null; }
        }
        if (_listener is null) throw new IOException("Can't open a network port.");
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _ = Task.Run(() => Housekeeping(_cts.Token));
        if (discovery)
        {
            _discovery = new Discovery(() => Self, () => Settings.Discoverable);
            _discovery.Seen += OnSeen;
            _discovery.Start();
        }
        await Task.CompletedTask;
    }

    void LoadSessions()
    {
        if (!Directory.Exists(SessionsDir)) return;
        foreach (var f in Directory.GetFiles(SessionsDir, "*.json"))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("in-") && Store.Load<IncomingSession.Saved>(f) is { } si && si.Id.Length > 0)
                Track(IncomingSession.FromSaved(si));
            else if (name.StartsWith("out-") && Store.Load<OutgoingSession.Saved>(f) is { } so && so.Id.Length > 0)
                Track(OutgoingSession.FromSaved(so, () => Self, Resolve, SendOptionsNow));
        }
    }

    void Track(IncomingSession s)
    {
        s.Changed += () => { _dirty[s.Id] = DateTime.UtcNow; Bump(); };
        _in[s.Id] = s;
    }

    void Track(OutgoingSession s)
    {
        s.PrepareSocket = _host.PrepareSocket;
        s.Changed += () =>
        {
            Persist(s);
            Bump();
            if (s.State is TransferState.Completed or TransferState.Failed) try { _host.TransferFinished(View(s)); } catch { }
        };
        _out[s.Id] = s;
    }

    void Persist(IncomingSession s) { try { Store.Save(InPath(s.Id), s.ToSaved()); } catch { } }
    void Persist(OutgoingSession s) { try { Store.Save(OutPath(s.Id), s.ToSaved()); } catch { } }

    async Task Housekeeping(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch { return; }
            foreach (var id in _dirty.Keys.ToList())
                if (_dirty.TryRemove(id, out _) && _in.TryGetValue(id, out var s)) Persist(s);
            // Active transfers persist their progress every few seconds.
            if (Environment.TickCount64 % 3 == 0)
            {
                foreach (var s in _in.Values.Where(x => x.State == TransferState.Active)) Persist(s);
                foreach (var s in _out.Values.Where(x => x.State == TransferState.Active)) Persist(s);
            }
            // A receive that went quiet is waiting for the sender to come back.
            foreach (var s in _in.Values.Where(x => x.State == TransferState.Active && DateTime.UtcNow - x.LastActivity > TimeSpan.FromSeconds(20)))
                s.SetState(TransferState.Interrupted, "Waiting for the sender.");
            foreach (var (id, p) in _peers.Where(p => !p.Value.Manual && DateTime.UtcNow - p.Value.Seen > TimeSpan.FromMinutes(10)).ToList())
                _peers.TryRemove(id, out _);
            Bump();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _discovery?.Dispose();
        _listener?.Stop();
        foreach (var s in _out.Values.Where(x => !States.IsFinal(x.State)))
        {
            s.Pause();
            Persist(s);
        }
        foreach (var s in _in.Values) { s.CloseFiles(); Persist(s); }
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ peers

    void OnSeen(DeviceInfo dev, IPAddress addr)
    {
        bool isNew = !_peers.ContainsKey(dev.Id);
        _peers.AddOrUpdate(dev.Id, (dev, addr, DateTime.UtcNow, false), (_, old) => (dev, addr, DateTime.UtcNow, old.Manual));
        if (isNew) Bump();
    }

    IPEndPoint? Resolve(string deviceId) =>
        _peers.TryGetValue(deviceId, out var p) ? new IPEndPoint(p.Addr, p.Dev.Port) : null;

    public IReadOnlyList<PeerView> Peers()
    {
        var trusted = Settings.TrustedDevices;
        return _peers.Values
            .Select(p => new PeerView(p.Dev.Id, p.Dev.Name, p.Dev.Platform, p.Addr.ToString(), p.Dev.Port,
                DateTime.UtcNow - p.Seen < PeerExpiry, trusted.Contains(p.Dev.Id), p.Manual, p.Seen))
            .OrderByDescending(p => p.Online).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds a device by IP address (optionally "ip:port").</summary>
    public async Task<PeerView?> AddManualAsync(string address, CancellationToken ct = default)
    {
        int port = Proto.TcpPort;
        address = address.Trim();
        if (IPEndPoint.TryParse(address, out var ep) && ep.Port != 0) { port = ep.Port; address = ep.Address.ToString(); }
        if (!IPAddress.TryParse(address, out var ip))
        {
            try { ip = (await Dns.GetHostAddressesAsync(address, AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
            catch { ip = null; }
            if (ip is null) return null;
        }
        var dev = await Discovery.HelloAsync(ip, port, TimeSpan.FromSeconds(3), ct, _host.PrepareSocket);
        if (dev is null || dev.Id == Settings.DeviceId) return null;
        _peers[dev.Id] = (dev, ip, DateTime.UtcNow, true);
        Bump();
        return Peers().First(p => p.Id == dev.Id);
    }

    public void ForgetPeer(string id) { _peers.TryRemove(id, out _); Bump(); }

    public void Refresh() => _discovery?.Announce(query: true);

    public Task ScanAsync(CancellationToken ct = default) => _discovery?.ScanAsync(Proto.TcpPort, ct) ?? Task.CompletedTask;

    public void SetTrusted(string deviceId, bool trusted) =>
        UpdateSettings(s => { s.TrustedDevices.Remove(deviceId); if (trusted) s.TrustedDevices.Add(deviceId); });

    // ------------------------------------------------------------------ sending

    SendOptions SendOptionsNow() { var s = Settings; return new SendOptions(s.Streams, s.ChunkMB * 1024 * 1024, s.Verify); }

    /// <summary>Builds the item list for local files and folders (folders keep their structure).</summary>
    public static List<SourceItem> Collect(IEnumerable<string> paths, string? text = null)
    {
        var items = new List<SourceItem>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        string Unique(string name)
        {
            var b = name; int k = 2;
            while (!names.Add(name)) name = $"{Path.GetFileNameWithoutExtension(b)} ({k++}){Path.GetExtension(b)}";
            return name;
        }
        foreach (var raw in paths)
        {
            var p = Path.GetFullPath(raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (File.Exists(p))
            {
                var fi = new FileInfo(p);
                items.Add(new SourceItem(new ItemInfo(n++, Unique(fi.Name), fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(), ItemType.File), p));
            }
            else if (Directory.Exists(p))
            {
                var root = Unique(new DirectoryInfo(p).Name is { Length: > 0 } nm ? nm : "Folder");
                items.Add(new SourceItem(new ItemInfo(n++, root, 0, 0, ItemType.Dir), null));
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var d in Directory.EnumerateDirectories(p, "*", opts))
                    items.Add(new SourceItem(new ItemInfo(n++, root + "/" + Path.GetRelativePath(p, d).Replace('\\', '/'), 0, 0, ItemType.Dir), null));
                foreach (var f in Directory.EnumerateFiles(p, "*", opts))
                {
                    var fi = new FileInfo(f);
                    items.Add(new SourceItem(new ItemInfo(n++, root + "/" + Path.GetRelativePath(p, f).Replace('\\', '/'), fi.Length,
                        new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(), ItemType.File), f));
                }
            }
        }
        if (!string.IsNullOrEmpty(text))
            items.Add(new SourceItem(new ItemInfo(n++, "Text", System.Text.Encoding.UTF8.GetByteCount(text), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ItemType.Text, text), null));
        return items;
    }

    /// <summary>Starts one transfer per target device. Returns their ids.</summary>
    public IReadOnlyList<string> Send(IEnumerable<string> deviceIds, IReadOnlyList<SourceItem> items)
    {
        if (items.Count == 0) throw new ArgumentException("Nothing to send.");
        var ids = new List<string>();
        foreach (var id in deviceIds.Distinct())
        {
            if (!_peers.TryGetValue(id, out var p)) continue;
            var s = new OutgoingSession(Guid.NewGuid().ToString("N"), p.Dev, new IPEndPoint(p.Addr, p.Dev.Port), items, () => Self, Resolve, SendOptionsNow);
            Track(s);
            Persist(s);
            s.Start();
            ids.Add(s.Id);
        }
        Bump();
        return ids;
    }

    // ------------------------------------------------------------------ control

    public bool Pause(string id)
    {
        if (_out.TryGetValue(id, out var o)) { o.Pause(); return true; }
        if (_in.TryGetValue(id, out var i) && i.State is TransferState.Active or TransferState.Interrupted) { i.SetState(TransferState.Paused); return true; }
        return false;
    }

    public bool Resume(string id)
    {
        if (_out.TryGetValue(id, out var o)) { if (o.State is TransferState.Failed or TransferState.Interrupted) o.Retry(); else o.Resume(); return true; }
        if (_in.TryGetValue(id, out var i) && i.State == TransferState.Paused) { i.SetState(TransferState.Active); return true; }
        return false;
    }

    public bool Cancel(string id)
    {
        if (_out.TryGetValue(id, out var o)) { o.Cancel(); return true; }
        if (_in.TryGetValue(id, out var i))
        {
            if (_decisions.TryGetValue(id, out var d)) { d.TrySetResult((false, false)); return true; }
            if (States.IsFinal(i.State)) return false;
            i.SetState(TransferState.Cancelled, "Cancelled.");
            i.Discard();
            Persist(i);
            return true;
        }
        return false;
    }

    public bool Accept(string id, bool trust)
    {
        if (!_decisions.TryGetValue(id, out var d)) return false;
        return d.TrySetResult((true, trust));
    }

    public bool Decline(string id) => _decisions.TryGetValue(id, out var d) && d.TrySetResult((false, false));

    /// <summary>Removes a finished transfer from history.</summary>
    public bool Remove(string id)
    {
        if (_out.TryGetValue(id, out var o) && (States.IsFinal(o.State) || o.State is TransferState.Interrupted or TransferState.Paused))
        {
            if (!States.IsFinal(o.State)) o.Cancel();
            _out.TryRemove(id, out _); Store.Delete(OutPath(id)); Bump(); return true;
        }
        if (_in.TryGetValue(id, out var i) && (States.IsFinal(i.State) || i.State is TransferState.Interrupted or TransferState.Paused))
        {
            if (!States.IsFinal(i.State)) i.Discard();
            _in.TryRemove(id, out _); Store.Delete(InPath(id)); Bump(); return true;
        }
        return false;
    }

    public void ClearHistory()
    {
        foreach (var o in _out.Values.Where(x => States.IsFinal(x.State)).ToList()) { _out.TryRemove(o.Id, out _); Store.Delete(OutPath(o.Id)); }
        foreach (var i in _in.Values.Where(x => States.IsFinal(x.State)).ToList()) { _in.TryRemove(i.Id, out _); Store.Delete(InPath(i.Id)); }
        Bump();
    }

    // ------------------------------------------------------------------ views

    PeerView PeerOf(DeviceInfo d)
    {
        if (_peers.TryGetValue(d.Id, out var p))
            return new PeerView(d.Id, p.Dev.Name, p.Dev.Platform, p.Addr.ToString(), p.Dev.Port, DateTime.UtcNow - p.Seen < PeerExpiry, Settings.TrustedDevices.Contains(d.Id), p.Manual, p.Seen);
        return new PeerView(d.Id, d.Name, d.Platform, "", d.Port, false, Settings.TrustedDevices.Contains(d.Id), false, DateTime.MinValue);
    }

    static string TitleOf(IReadOnlyList<ItemInfo> items)
    {
        var top = items.Where(i => !i.Path.Contains('/')).ToList();
        if (top.Count == 0) top = items.Take(1).ToList();
        var first = top[0].Type == ItemType.Text ? "Text" : top[0].Path;
        return top.Count == 1 ? first : $"{first} and {top.Count - 1} more";
    }

    static IReadOnlyList<string> PreviewOf(IReadOnlyList<ItemInfo> items) =>
        items.Where(i => !i.Path.Contains('/')).Take(8).Select(i => i.Type == ItemType.Dir ? i.Path + "/" : i.Type == ItemType.Text ? "Text" : i.Path).ToList();

    static double? Eta(long total, long done, double rate) => rate > 1 && done < total ? (total - done) / rate : null;

    public TransferView View(OutgoingSession s)
    {
        long done = s.State == TransferState.Completed ? s.TotalBytes : s.Live;
        double rate = s.State == TransferState.Active ? s.Rate.BytesPerSecond : 0;
        var st = s.State;
        return new TransferView(s.Id, Direction.Send, PeerOf(s.Target), st, s.Error, TitleOf(s.Items),
            s.Items.Count(i => i.Type == ItemType.File), s.Items.Count(i => i.Type == ItemType.Dir), s.TotalBytes, done, rate, Eta(s.TotalBytes, done, rate),
            st == TransferState.Active ? s.CurrentFile : null, s.Started, s.Finished, null, null,
            s.Items.FirstOrDefault(i => i.Type == ItemType.Text)?.Text, true, null, PreviewOf(s.Items),
            CanPause: st is TransferState.Active or TransferState.Connecting or TransferState.Reconnecting or TransferState.Waiting or TransferState.PausedByPeer,
            CanResume: st is TransferState.Paused or TransferState.Interrupted or TransferState.Failed,
            CanCancel: !States.IsFinal(st),
            CanRetry: st is TransferState.Failed or TransferState.Declined);
    }

    public TransferView View(IncomingSession s)
    {
        long done = s.State == TransferState.Completed ? s.TotalBytes : Math.Min(s.TotalBytes, s.Live);
        double rate = s.State == TransferState.Active ? s.Rate.BytesPerSecond : 0;
        var st = s.State;
        var saveDir = string.IsNullOrEmpty(s.Root) ? Settings.SaveDir : s.Root;
        var (spaceOk, warning) = CheckTarget(saveDir, s);
        return new TransferView(s.Id, Direction.Receive, PeerOf(s.From), st, s.Error, TitleOf(s.Items),
            s.Items.Count(i => i.Type == ItemType.File), s.Items.Count(i => i.Type == ItemType.Dir), s.TotalBytes, done, rate, Eta(s.TotalBytes, done, rate),
            null, s.Started, s.Finished, st == TransferState.Completed ? s.OpenTarget() : null, saveDir,
            s.Items.FirstOrDefault(i => i.Type == ItemType.Text)?.Text, spaceOk, warning, PreviewOf(s.Items),
            CanPause: st == TransferState.Active,
            CanResume: st == TransferState.Paused,
            CanCancel: !States.IsFinal(st),
            CanRetry: false);
    }

    const long Fat32Max = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>Free space, and whether the drive's format can hold the largest file (FAT32 stops at 4 GB).</summary>
    static (bool SpaceOk, string? Warning) CheckTarget(string saveDir, IncomingSession s)
    {
        if (States.IsFinal(s.State)) return (true, null);
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(saveDir));
            if (string.IsNullOrEmpty(root)) return (true, null);
            var drive = new DriveInfo(root);
            bool spaceOk = drive.AvailableFreeSpace >= s.TotalBytes - s.Done;
            long largest = s.Items.Where(i => i.Type == ItemType.File).Select(i => i.Size).DefaultIfEmpty(0).Max();
            if (largest > Fat32Max && drive.DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) && !drive.DriveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
                return (spaceOk, "The save drive is FAT32 and can't hold files over 4 GB.");
            return (spaceOk, spaceOk ? null : "Not enough free space.");
        }
        catch { return (true, null); }
    }

    public IReadOnlyList<TransferView> Transfers() =>
        _out.Values.Select(View).Concat(_in.Values.Select(View)).OrderByDescending(t => t.Started).ToList();

    public TransferView? Transfer(string id) =>
        _out.TryGetValue(id, out var o) ? View(o) : _in.TryGetValue(id, out var i) ? View(i) : null;

    public SelfView SelfInfo() =>
        new(Settings.DeviceId, Settings.DeviceName, Platform, Port, Discovery.LocalIPv4().Select(a => a.Address.ToString()).Distinct().ToList());

    // ------------------------------------------------------------------ server

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket sock;
            try { sock = await _listener!.AcceptSocketAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { continue; }
            _ = Task.Run(() => HandleAsync(sock, ct));
        }
    }

    async Task HandleAsync(Socket sock, CancellationToken ct)
    {
        await using var c = new Conn(sock);
        byte[]? bufA = null, bufB = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await c.ReadMessageAsync(ct, TimeSpan.FromMinutes(10));
                if (msg is null) return;
                JsonObject reply;
                switch (msg.Str("op"))
                {
                    case "hello":
                        reply = Json.Ok(new JsonObject { ["device"] = Json.From(Self) });
                        if (c.Remote != null && Settings.Discoverable) { /* nothing else: hello is a question */ }
                        break;
                    case "offer":
                        reply = await HandleOfferAsync(msg, c, ct);
                        break;
                    case "data":
                        bufA ??= new byte[1024 * 1024];
                        bufB ??= new byte[1024 * 1024];
                        reply = await HandleDataAsync(msg, c, bufA, bufB, ct);
                        break;
                    case "status":
                        reply = WithSession(msg, s => Json.Ok(new JsonObject
                        {
                            ["state"] = WireState(s.State),
                            ["have"] = Json.From(s.HaveForWire()),
                        }));
                        break;
                    case "pause":
                        reply = WithSession(msg, s => { if (s.State == TransferState.Active || s.State == TransferState.Interrupted) s.SetState(TransferState.PausedByPeer); return Json.Ok(); });
                        break;
                    case "resume":
                        reply = WithSession(msg, s => { if (s.State == TransferState.PausedByPeer) s.SetState(TransferState.Active); return Json.Ok(); });
                        break;
                    case "cancel":
                        reply = WithSession(msg, s =>
                        {
                            if (!States.IsFinal(s.State)) { s.SetState(TransferState.Cancelled, "Cancelled by the sender."); s.Discard(); Persist(s); }
                            return Json.Ok();
                        });
                        break;
                    case "done":
                        reply = WithSession(msg, HandleDone);
                        break;
                    default:
                        reply = Json.Error("unsupported");
                        break;
                }
                await c.WriteMessageAsync(reply, ct);
            }
        }
        catch (Exception ex) { if (Environment.GetEnvironmentVariable("CFS_DEBUG") == "1") Console.Error.WriteLine("server: " + ex); }
    }

    static string WireState(TransferState s) => s switch
    {
        TransferState.Paused => "paused",
        TransferState.Cancelled or TransferState.Declined => "cancelled",
        TransferState.Completed => "done",
        _ => "active",
    };

    JsonObject WithSession(JsonObject msg, Func<IncomingSession, JsonObject> f)
    {
        if (msg.Str("session") is not { } id || !_in.TryGetValue(id, out var s)) return Json.Error("unknown");
        if (s.Key != msg.Str("key")) return Json.Error("forbidden");
        return f(s);
    }

    async Task<JsonObject> HandleOfferAsync(JsonObject msg, Conn c, CancellationToken ct)
    {
        var id = msg.Str("session");
        var from = Json.To<DeviceInfo>(msg["from"]);
        var items = Json.To<List<ItemInfo>>(msg["items"]);
        if (string.IsNullOrEmpty(id) || from is null || items is null || items.Count == 0) return Json.Error("bad offer");
        if (id.Length > 64 || items.Count > 2_000_000) return Json.Error("bad offer");
        items = items.Select((it, k) => it with { I = k }).ToList();

        if (c.Remote != null)
        {
            // An offer proves the device is here, even when discovery is blocked.
            bool isNew = !_peers.ContainsKey(from.Id);
            _peers.AddOrUpdate(from.Id, (from, c.Remote.Address, DateTime.UtcNow, false), (_, o) => (from, c.Remote.Address, DateTime.UtcNow, o.Manual));
            if (isNew) Bump();
        }

        // Re-offer of a known session: resume without asking again.
        if (_in.TryGetValue(id, out var known))
        {
            if (known.From.Id != from.Id) return Json.Error("forbidden");
            if (known.State is TransferState.Completed) return Json.Ok(new JsonObject { ["accepted"] = true, ["key"] = known.Key, ["have"] = Json.From(known.HaveForWire()) });
            if (known.State is TransferState.Cancelled) return Json.Ok(new JsonObject { ["accepted"] = false, ["reason"] = "cancelled" });
            if (known.State is TransferState.Declined) return Json.Ok(new JsonObject { ["accepted"] = false, ["reason"] = "declined" });
            if (known.State is TransferState.Waiting) return Json.Error("busy");
            known.FromAddress = c.Remote?.Address;
            if (known.State is TransferState.Interrupted or TransferState.PausedByPeer) known.SetState(TransferState.Active);
            return Json.Ok(new JsonObject { ["accepted"] = true, ["key"] = known.Key, ["have"] = Json.From(known.HaveForWire()) });
        }

        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var s = new IncomingSession(id, from, c.Remote?.Address, items, key);
        Track(s);
        Bump();

        var settings = Settings;
        bool trusted = settings.TrustedDevices.Contains(from.Id);
        var view = View(s);
        if (view.Warning != null && (!settings.AskBeforeReceive || trusted))
        {
            s.SetState(TransferState.Declined, view.Warning);
            Persist(s);
            return Json.Ok(new JsonObject { ["accepted"] = false, ["reason"] = view.SpaceOk ? "fat32" : "space" });
        }

        bool accept;
        if (!settings.AskBeforeReceive || trusted) accept = true;
        else
        {
            var tcs = new TaskCompletionSource<(bool, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _decisions[id] = tcs;
            try { _host.IncomingRequest(view); } catch { }
            var deadline = DateTime.UtcNow + DecisionTimeout;
            (bool Accept, bool Trust) decision = (false, false);
            bool timedOut = true;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    var t = await Task.WhenAny(tcs.Task, Task.Delay(5000, ct));
                    if (t == tcs.Task) { decision = tcs.Task.Result; timedOut = false; break; }
                    await c.KeepAliveAsync(ct); // also notices if the sender gave up
                }
            }
            catch
            {
                _decisions.TryRemove(id, out _);
                s.SetState(TransferState.Cancelled, "The sender cancelled.");
                Persist(s);
                throw;
            }
            _decisions.TryRemove(id, out _);
            accept = decision.Accept;
            if (decision.Trust) SetTrusted(from.Id, true);
            if (!accept)
            {
                s.SetState(TransferState.Declined, timedOut ? "No answer." : null);
                Persist(s);
                return Json.Ok(new JsonObject { ["accepted"] = false, ["reason"] = timedOut ? "timeout" : "declined" });
            }
        }

        var root = Settings.SaveDir;
        try { Directory.CreateDirectory(root); }
        catch (Exception ex)
        {
            s.SetState(TransferState.Failed, "Can't use the save folder: " + ex.Message);
            return Json.Ok(new JsonObject { ["accepted"] = false, ["reason"] = "declined" });
        }
        s.Accept(root, Settings.Conflict, Settings.OrganizeByType);
        if (s.Items.All(i => i.Type != ItemType.File)) { s.Finish(); }
        Persist(s);
        return Json.Ok(new JsonObject { ["accepted"] = true, ["key"] = key, ["have"] = Json.From(s.HaveForWire()) });
    }

    async Task<JsonObject> HandleDataAsync(JsonObject msg, Conn c, byte[] bufA, byte[] bufB, CancellationToken ct)
    {
        var parts = (msg["parts"] as JsonArray)?.OfType<JsonObject>().Select(p => ((int)p.Long("i"), p.Long("off"), p.Long("len"))).ToList();
        if (parts is null) throw new IOException("Bad data request.");
        string? error = null;
        _in.TryGetValue(msg.Str("session") ?? "", out var s);
        if (s is null) error = "unknown";
        else if (s.Key != msg.Str("key")) error = "forbidden";
        else if (s.State is TransferState.Paused) error = "paused";
        else if (s.State is TransferState.Cancelled or TransferState.Declined or TransferState.Failed) error = "cancelled";
        else if (s.State is TransferState.Interrupted or TransferState.PausedByPeer) s.SetState(TransferState.Active);

        bool write = error is null;
        foreach (var (i, off, len) in parts)
        {
            if (s is null || i < 0 || i >= s.Items.Count) { await Drain(c, len, bufA, ct); continue; }
            if (!await s.ReceivePartAsync(c, i, off, len, write, bufA, bufB, ct)) error ??= "crc";
        }
        if (s != null && write) _dirty[s.Id] = DateTime.UtcNow;
        return error is null ? Json.Ok() : Json.Error(error);
    }

    static async Task Drain(Conn c, long len, byte[] buf, CancellationToken ct)
    {
        while (len > 0)
        {
            int n = (int)Math.Min(buf.Length, len);
            await c.ReadExactAsync(buf.AsMemory(0, n), ct);
            len -= n;
        }
        await c.ReadUInt32Async(ct);
    }

    JsonObject HandleDone(IncomingSession s)
    {
        if (s.State == TransferState.Completed) return Json.Ok();
        if (s.State is TransferState.Cancelled or TransferState.Declined) return Json.Error("cancelled");
        try
        {
            var missing = s.Finish();
            if (missing.Count > 0) return Json.Error("incomplete");
            Persist(s);
            try { _host.TransferFinished(View(s)); } catch { }
            return Json.Ok();
        }
        catch (Exception ex)
        {
            s.SetState(TransferState.Failed, ex.Message);
            Persist(s);
            return Json.Error(ex.Message);
        }
    }
}
