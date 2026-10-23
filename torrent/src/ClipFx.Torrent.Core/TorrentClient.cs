using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.PiecePicking;
using MonoTorrent.Trackers;

namespace ClipFx.Torrents;

/// <summary>Things only the desktop shell can do.</summary>
public interface ITorrentHost
{
    void Finished(TorrentView t) { }
    void KeepAwake(bool on) { }
    void AllDone(string action) { }
}

public sealed record AddRequest(string? Source, string? PreviewId, string? SaveDir, bool? Paused, bool? Sequential, int[]? Skip);
public sealed record AddResult(string Id, string Name, bool Existing);

/// <summary>
/// Owns the torrent engine and every torrent's lifecycle: queueing, seeding policy, recovery after
/// network loss or disk trouble, and state that survives restarts and crashes.
/// </summary>
public sealed class TorrentClient : IAsyncDisposable
{
    sealed class Item(Entry e)
    {
        public Entry E = e;
        public TorrentManager? M;
        public string? AttachError;
        public string? Blocked;                 // why we're holding it back (disk full, folder missing)
        public long LastRx, LastTx;
        public double DownAvg;
        public DateTime RunningSince, SlowSince = DateTime.MaxValue, NoPeersSince = DateTime.MaxValue, LastReannounce;
        public DateTime ErrorAt;
        public int ErrorRetries;
        public bool WasDone, Moving, Busy, MetaApplied;
        public string Kind = "file";
    }

    readonly string _dir, _cache, _meta, _settingsPath, _entriesPath;
    readonly ITorrentHost _host;
    readonly Dictionary<string, Item> _items = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly ConcurrentDictionary<string, (Source Src, DateTime At)> _previews = new();
    readonly CancellationTokenSource _stop = new();
    readonly HashSet<string> _extra = new(StringComparer.OrdinalIgnoreCase);
    ClientEngine _engine = null!;
    Task? _loop;
    bool _dirty, _sawIncoming;
    volatile bool _online = true;
    DateTime _lastSave = DateTime.UtcNow, _netChangedAt = DateTime.MinValue;

    internal TimeSpan LonelyAfter = TimeSpan.FromSeconds(45);
    internal TimeSpan ReannounceEvery = TimeSpan.FromMinutes(2);

    public Settings Settings { get; private set; }
    public string WhenDone { get; set; } = "nothing";
    public event Action? Changed;
    public string Version { get; } = typeof(TorrentClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public TorrentClient(string dataDir, ITorrentHost? host = null)
    {
        _dir = Directory.CreateDirectory(dataDir).FullName;
        _cache = Directory.CreateDirectory(Path.Combine(_dir, "cache")).FullName;
        _meta = Directory.CreateDirectory(Path.Combine(_dir, "torrents")).FullName;
        _settingsPath = Path.Combine(_dir, "settings.json");
        _entriesPath = Path.Combine(_dir, "torrents.json");
        _host = host ?? new NoHost();
        Settings = Store.Load(_settingsPath, () => new Settings());
        if (Settings.Port is <= 0 or > 65535)
        {
            Settings.Port = Random.Shared.Next(20000, 60000);
            Store.Save(_settingsPath, Settings);
        }
        RebuildExtraTrackers();
    }

    sealed class NoHost : ITorrentHost { }

    // ================================================================ lifecycle

    public async Task StartAsync()
    {
        _engine = new ClientEngine(EngineSettingsFor(Settings), Factories.Default.WithDhtCreator(() => new BootstrapDht(Factories.Default.CreateDht())));
        _engine.CriticalException += (_, _) => Touch();
        var entries = Store.Load(_entriesPath, () => new List<Entry>());
        foreach (var e in entries.OrderBy(e => e.Order))
        {
            var item = new Item(e);
            lock (_items) _items[e.Id] = item;
            await AttachAsync(item, null);
        }
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;
        _online = NetworkInterface.GetIsNetworkAvailable();
        await ReconcileAsync();
        _loop = Task.Run(LoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability;
        try { if (_loop != null) await _loop; } catch { }
        await _gate.WaitAsync();
        try
        {
            CountTraffic();
            SaveEntries();
            // Stopping writes fast-resume data, so the next start skips the full hash check.
            try { await _engine.StopAllAsync(TimeSpan.FromSeconds(6)).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            _engine.Dispose();
        }
        finally { _gate.Release(); }
        _host.KeepAwake(false);
    }

    async Task LoopAsync()
    {
        var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await tick.WaitForNextTickAsync(_stop.Token))
            {
                try { await ReconcileAsync(); } catch { }
                Touch();
            }
        }
        catch (OperationCanceledException) { }
    }

    void Touch() => Changed?.Invoke();

    // ================================================================ engine settings

    EngineSettings EngineSettingsFor(Settings s)
    {
        var enc = s.Encryption switch
        {
            "require" => new List<EncryptionType> { EncryptionType.RC4Full, EncryptionType.RC4Header },
            "off" => new List<EncryptionType> { EncryptionType.PlainText },
            _ => new List<EncryptionType> { EncryptionType.PlainText, EncryptionType.RC4Header, EncryptionType.RC4Full },
        };
        var b = new EngineSettingsBuilder
        {
            CacheDirectory = _cache,
            AllowPortForwarding = s.PortForwarding,
            AllowLocalPeerDiscovery = s.LocalPeers,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            FastResumeMode = FastResumeMode.BestEffort,
            DiskCacheBytes = Math.Clamp(s.DiskCacheMB, 0, 1024) << 20,
            MaximumConnections = Math.Clamp(s.ConnectionsTotal, 20, 5000),
            MaximumHalfOpenConnections = Math.Clamp(s.HalfOpen, 4, 500),
            MaximumDownloadRate = (int)Math.Min(int.MaxValue, Math.Max(0, s.DownloadLimitKB) * 1024L),
            MaximumUploadRate = (int)Math.Min(int.MaxValue, Math.Max(0, s.UploadLimitKB) * 1024L),
            MaximumOpenFiles = 256,
            AllowedEncryption = enc,
            // Use HTTP seeds straight away instead of waiting for peers to be slow.
            WebSeedDelay = TimeSpan.Zero,
            WebSeedSpeedTrigger = int.MaxValue,
            ListenEndPoints = new()
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Any, s.Port),
                ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, s.Port),
            },
            DhtEndPoint = s.Dht ? new IPEndPoint(IPAddress.Any, s.Port) : null,
        };
        return b.ToSettings();
    }

    TorrentSettings TorrentSettingsFor(Settings s) => new TorrentSettingsBuilder
    {
        AllowDht = s.Dht,
        AllowPeerExchange = s.PeerExchange,
        MaximumConnections = Math.Clamp(s.ConnectionsPerTorrent, 10, 2000),
        UploadSlots = Math.Clamp(s.UploadSlots, 2, 100),
        CreateContainingDirectory = true,
    }.ToSettings();

    void RebuildExtraTrackers()
    {
        lock (_extra)
        {
            _extra.Clear();
            if (Settings.ExtraTrackers) foreach (var t in Trackers.Public) _extra.Add(t);
            foreach (var t in Trackers.Parse(Settings.CustomTrackers)) _extra.Add(t);
        }
    }

    string[] ExtraTrackers() { lock (_extra) return _extra.ToArray(); }

    public async Task UpdateSettingsAsync(Action<Settings> change)
    {
        await _gate.WaitAsync();
        try
        {
            var before = System.Text.Json.JsonSerializer.Serialize(Settings);
            change(Settings);
            Settings.SeedRatio = Math.Clamp(Settings.SeedRatio, 0.1, 100);
            Settings.ActiveDownloads = Math.Clamp(Settings.ActiveDownloads, 0, 100);
            if (Settings.Port is <= 0 or > 65535) Settings.Port = Random.Shared.Next(20000, 60000);
            Store.Save(_settingsPath, Settings);
            if (before == System.Text.Json.JsonSerializer.Serialize(Settings)) return;
            RebuildExtraTrackers();
            if (_engine == null) return; // not started yet: the engine picks these up on start
            await _engine.UpdateSettingsAsync(EngineSettingsFor(Settings));
            var ts = TorrentSettingsFor(Settings);
            foreach (var it in Snapshot()) if (it.M != null) try { await it.M.UpdateSettingsAsync(ts); } catch { }
            foreach (var it in Snapshot()) if (it.M is { } m && m.HasMetadata && !m.Torrent!.IsPrivate) await AddTrackersAsync(m, ExtraTrackers());
        }
        finally { _gate.Release(); }
        await ReconcileAsync();
        Touch();
    }

    // ================================================================ adding

    /// <summary>Reads a link or file and, for magnets, asks peers for the file list.</summary>
    public async Task<Preview> PreviewAsync(string input, bool fetchMetadata, CancellationToken ct)
    {
        var src = await Sources.ResolveAsync(input, ct);
        var id = src.Id;
        string? existing;
        lock (_items) existing = _items.ContainsKey(id) ? id : null;
        if (existing == null && src.Torrent == null && fetchMetadata)
        {
            var cached = Path.Combine(_meta, id + ".torrent");
            if (File.Exists(cached) && Torrent.TryLoad(cached, out var t0)) src = new Source { Torrent = t0, TorrentBytes = File.ReadAllBytes(cached) };
            else
            {
                // Metadata only: keep the link's own trackers first so a dead public one can't hide them.
                var magnet = src.Magnet!.AnnounceUrls.Count > 0 ? src.Magnet! : WithExtraTrackers(src.Magnet!);
                var data = await _engine.DownloadMetadataAsync(magnet, ct);
                var bytes = Sources.MetadataToTorrent(data, magnet);
                src = Sources.FromBytes(bytes);
            }
        }
        _previews[id] = (src, DateTime.UtcNow);
        foreach (var old in _previews.Where(p => DateTime.UtcNow - p.Value.At > TimeSpan.FromMinutes(30)).Select(p => p.Key).ToList()) _previews.TryRemove(old, out _);

        var t = src.Torrent;
        var files = t == null ? [] : t.Files.Select((f, i) => new PreviewFile(i, f.Path, f.Length)).ToList();
        int trackers = t != null ? t.AnnounceUrls.Sum(tier => tier.Count) : src.Magnet!.AnnounceUrls.Count;
        return new Preview(id, src.Name, t?.Size ?? src.Magnet?.Size ?? 0, t != null, t?.IsPrivate ?? false, trackers, files, existing);
    }

    public async Task<AddResult> AddAsync(AddRequest req, CancellationToken ct = default)
    {
        Source src;
        if (req.PreviewId != null && _previews.TryGetValue(req.PreviewId, out var p)) src = p.Src;
        else if (!string.IsNullOrWhiteSpace(req.Source)) src = await Sources.ResolveAsync(req.Source, ct);
        else throw new UserError("Nothing to add.");

        var id = src.Id;
        await _gate.WaitAsync(ct);
        try
        {
            lock (_items)
                if (_items.TryGetValue(id, out var have))
                {
                    // Same torrent again: keep the one we have but learn any new trackers.
                    var urls = src.Torrent?.AnnounceUrls.SelectMany(x => x) ?? src.Magnet?.AnnounceUrls ?? [];
                    if (have.M != null && !(have.M.Torrent?.IsPrivate ?? false)) _ = AddTrackersAsync(have.M, urls.ToArray());
                    return new AddResult(id, have.E.Name, true);
                }

            var saveDir = string.IsNullOrWhiteSpace(req.SaveDir) ? Settings.SaveDir : req.SaveDir!;
            try { Directory.CreateDirectory(saveDir); }
            catch { throw new UserError("Can't save to that folder."); }

            if (src.TorrentBytes != null) await File.WriteAllBytesAsync(Path.Combine(_meta, id + ".torrent"), src.TorrentBytes, ct);
            var magnet = src.Magnet ?? new MagnetLink(src.Torrent!.InfoHashes, src.Torrent.Name,
                src.Torrent.AnnounceUrls.SelectMany(x => x).Distinct().ToList(), null, src.Torrent.Size);
            int order;
            lock (_items) order = _items.Count == 0 ? 0 : _items.Values.Max(i => i.E.Order) + 1;
            var e = new Entry
            {
                Id = id,
                Name = src.Name,
                Magnet = magnet.ToV1String(),
                SaveDir = saveDir,
                Paused = req.Paused ?? false,
                Sequential = req.Sequential ?? Settings.SequentialByDefault,
                Order = order,
                Skip = req.Skip is { Length: > 0 } ? req.Skip.Distinct().ToList() : null,
                Size = src.Torrent?.Size ?? src.Magnet?.Size ?? 0,
            };
            var item = new Item(e);
            lock (_items) _items[id] = item;
            await AttachAsync(item, src);
            _dirty = true;
            SaveEntries();
            _previews.TryRemove(id, out _);
            return new AddResult(id, e.Name, false);
        }
        finally { _gate.Release(); _ = ReconcileAsync().ContinueWith(_ => Touch()); }
    }

    MagnetLink WithExtraTrackers(MagnetLink m)
    {
        var list = m.AnnounceUrls.Concat(ExtraTrackers()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new MagnetLink(m.InfoHashes, m.Name, list, m.Webseeds, m.Size);
    }

    async Task AttachAsync(Item item, Source? src)
    {
        var e = item.E;
        try
        {
            TorrentManager m;
            var ts = TorrentSettingsFor(Settings);
            var tf = Path.Combine(_meta, e.Id + ".torrent");
            Torrent? t = src?.Torrent;
            if (t == null && File.Exists(tf)) Torrent.TryLoad(tf, out t);
            if (t != null)
            {
                m = await _engine.AddAsync(t, e.SaveDir, ts);
                if (!t.IsPrivate) await AddTrackersAsync(m, ExtraTrackers());
            }
            else
            {
                // A magnet's trackers would all share one tier, which is asked one tracker at a time
                // (and shuffled), so dead trackers delay live ones. One tier per tracker announces to all at once.
                var ml = MagnetLink.Parse(e.Magnet);
                m = await _engine.AddAsync(new MagnetLink(ml.InfoHashes, ml.Name, new List<string>(), ml.Webseeds, ml.Size), e.SaveDir, ts);
                await AddTrackersAsync(m, (ml.AnnounceUrls ?? []).Concat(ExtraTrackers()).ToArray());
            }
            item.M = m;
            item.AttachError = null;
            m.PeerConnected += (_, a) => { if (a.Peer.ConnectionDirection == MonoTorrent.Direction.Incoming) _sawIncoming = true; };
            m.TorrentStateChanged += (_, _) => Touch();
            if (m.HasMetadata) await OnMetadataAsync(item);
            item.WasDone = IsDone(m);
        }
        catch (Exception ex)
        {
            item.AttachError = ex is UserError ? ex.Message : "Couldn't load this torrent: " + ex.Message;
        }
    }

    /// <summary>Runs once the file list is known (immediately for .torrent, later for magnets).</summary>
    async Task OnMetadataAsync(Item item)
    {
        var m = item.M!;
        var e = item.E;
        var t = m.Torrent!;
        item.MetaApplied = true;
        e.Name = t.Name;
        e.Size = t.Size;
        item.Kind = KindOf(t);
        var tf = Path.Combine(_meta, e.Id + ".torrent");
        if (!File.Exists(tf) && File.Exists(m.MetadataPath)) try { File.Copy(m.MetadataPath, tf, true); } catch { }

        if (t.IsPrivate)
        {
            // Private trackers forbid sharing with outside trackers.
            foreach (var tier in m.TrackerManager.Tiers.ToList())
                foreach (var tr in tier.Trackers.ToList())
                    if (IsExtra(tr.Uri.OriginalString)) try { await m.TrackerManager.RemoveTrackerAsync(tr); } catch { }
        }
        if (e.Skip != null)
        {
            for (int i = 0; i < m.Files.Count; i++)
                if (e.Skip.Contains(i)) await m.SetFilePriorityAsync(m.Files[i], Priority.DoNotDownload);
            e.Priorities ??= new();
            foreach (var i in e.Skip) e.Priorities[i] = "skip";
            e.Skip = null;
            _dirty = true;
        }
        else if (e.Priorities != null)
        {
            foreach (var (i, p) in e.Priorities)
                if (i >= 0 && i < m.Files.Count) await m.SetFilePriorityAsync(m.Files[i], ToPriority(p));
        }
        if (e.Sequential) await ApplyPickerAsync(m, true);
        _dirty = true;
    }

    bool IsExtra(string url) { lock (_extra) return _extra.Contains(url); }

    static async Task AddTrackersAsync(TorrentManager m, string[] urls)
    {
        var have = new HashSet<string>(m.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.OriginalString), StringComparer.OrdinalIgnoreCase);
        foreach (var u in urls)
            if (have.Add(u) && Uri.TryCreate(u, UriKind.Absolute, out var uri))
                try { await m.TrackerManager.AddTrackerAsync(uri); } catch { }
    }

    static async Task ApplyPickerAsync(TorrentManager m, bool sequential)
    {
        var f = Factories.Default;
        var picker = sequential
            ? f.CreatePieceRequester(new PieceRequesterSettings(allowPrioritisation: true, allowRandomised: false, allowRarestFirst: false, maximumDuplicateRequests: 3))
            : f.CreatePieceRequester();
        await m.ChangePickerAsync(picker);
    }

    // ================================================================ the control loop

    static bool IsDone(TorrentManager m) => m.HasMetadata && (m.Complete || m.PartialProgress >= 100.0);
    static bool IsRunning(TorrentManager m) => m.State is not (TorrentState.Stopped or TorrentState.Stopping or TorrentState.Error or TorrentState.HashingPaused);

    List<Item> Snapshot() { lock (_items) return _items.Values.OrderBy(i => i.E.Order).ToList(); }

    async Task ReconcileAsync()
    {
        if (!await _gate.WaitAsync(0)) return; // another pass (or a user action) is already doing it
        try
        {
            var now = DateTime.UtcNow;
            CountTraffic();
            int activeDownloads = 0;
            bool anyDownloading = false, anyFinishedNow = false;

            foreach (var it in Snapshot())
            {
                var m = it.M;
                if (m == null)
                {
                    if (it.AttachError != null && now - it.ErrorAt > TimeSpan.FromSeconds(30)) { it.ErrorAt = now; await AttachAsync(it, null); }
                    continue;
                }
                if (it.Busy || it.Moving) continue;
                var e = it.E;

                if (m.HasMetadata && !it.MetaApplied) await OnMetadataAsync(it);

                // Engine error (disk unplugged, permissions, write failure): retry with backoff.
                if (m.State == TorrentState.Error)
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(300, 10 * Math.Pow(2, Math.Min(it.ErrorRetries, 5))));
                    if (it.ErrorAt == default) it.ErrorAt = now;
                    if (!e.Paused && now - it.ErrorAt > wait)
                    {
                        it.ErrorRetries++;
                        it.ErrorAt = now;
                        try { await m.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
                    }
                    else continue;
                }

                bool done = IsDone(m);
                if (done && !it.WasDone)
                {
                    it.WasDone = true;
                    e.CompletedAt ??= now;
                    e.SeedDone = false;
                    _dirty = true;
                    anyFinishedNow = true;
                    if (!string.IsNullOrWhiteSpace(Settings.MoveDoneTo) && !PathEquals(Settings.MoveDoneTo!, e.SaveDir))
                        _ = MoveAsync(e.Id, Settings.MoveDoneTo!);
                    var view = ViewOf(it);
                    if (view != null) try { _host.Finished(view); } catch { }
                }
                else if (!done && it.WasDone) { it.WasDone = false; e.CompletedAt = null; _dirty = true; }

                bool want;
                it.Blocked = null;
                if (e.Paused) want = false;
                else if (done)
                {
                    if (e.Forced) want = true;
                    else if (e.SeedDone) want = false;
                    else
                    {
                        var ratio = Ratio(e);
                        bool stop = Settings.Seeding == "stop" || (Settings.Seeding == "ratio" && ratio >= Settings.SeedRatio);
                        if (stop) { e.SeedDone = true; _dirty = true; }
                        want = !stop;
                    }
                }
                else
                {
                    anyDownloading = true;
                    bool slow = IsRunning(m) && m.State != TorrentState.Metadata && now - it.RunningSince > TimeSpan.FromSeconds(90) && it.DownAvg < 20 * 1024;
                    bool slot = e.Forced || Settings.ActiveDownloads == 0 || activeDownloads < Settings.ActiveDownloads || (IsRunning(m) && slow);
                    want = slot;
                    if (want && !slow) activeDownloads++;
                    if (want) it.Blocked = SpaceProblem(it);
                    if (it.Blocked != null) want = false;
                }

                if (want && !IsRunning(m))
                {
                    try
                    {
                        Directory.CreateDirectory(e.SaveDir);
                        await m.StartAsync();
                        it.RunningSince = now;
                        it.NoPeersSince = now;
                        it.SlowSince = DateTime.MaxValue;
                    }
                    catch (Exception ex) { it.Blocked = ex.Message; }
                }
                else if (!want && IsRunning(m))
                {
                    try { await m.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
                }

                if (IsRunning(m))
                {
                    if (m.State != TorrentState.Error) it.ErrorRetries = 0;
                    if (m.OpenConnections > 0) it.NoPeersSince = DateTime.MaxValue;
                    else if (it.NoPeersSince == DateTime.MaxValue) it.NoPeersSince = now;
                    // Nobody to download from: ask trackers and DHT again instead of waiting up to 30 minutes.
                    bool lonely = now - it.NoPeersSince > LonelyAfter && now - it.LastReannounce > ReannounceEvery;
                    bool netBack = _netChangedAt > it.LastReannounce && now - _netChangedAt > TimeSpan.FromSeconds(3);
                    if ((lonely || netBack) && _online && m.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata)
                    {
                        it.LastReannounce = now;
                        _ = Reannounce(m);
                    }
                }
            }

            _host.KeepAwake(Settings.KeepAwake && anyDownloading && Snapshot().Any(i => i.M is { } m && IsRunning(m) && !IsDone(m)));

            if (anyFinishedNow && WhenDone != "nothing" && !anyDownloading)
            {
                var action = WhenDone;
                WhenDone = "nothing";
                CountTraffic();
                SaveEntries();
                try { _host.AllDone(action); } catch { }
            }

            if (_dirty && now - _lastSave > TimeSpan.FromSeconds(5)) SaveEntries();
            else if (now - _lastSave > TimeSpan.FromSeconds(30)) SaveEntries();
        }
        finally { _gate.Release(); }
    }

    static async Task Reannounce(TorrentManager m)
    {
        try { await m.TrackerManager.AnnounceAsync(CancellationToken.None); } catch { }
        try { await m.DhtAnnounceAsync(); } catch { }
        try { await m.LocalPeerAnnounceAsync(); } catch { }
    }

    string? SpaceProblem(Item it)
    {
        var m = it.M!;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(it.E.SaveDir));
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "Save drive isn't connected.";
            if (!m.HasMetadata) return null;
            long need = Wanted(m) - DoneBytes(m);
            long free = new DriveInfo(root).AvailableFreeSpace;
            // Files we already started occupy space, so only the remainder counts.
            if (need > free + (16L << 20)) return $"Needs {Fmt(need)} free on {root.TrimEnd('\\', '/')}, only {Fmt(free)} left.";
        }
        catch { }
        return null;
    }

    static string Fmt(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    void CountTraffic()
    {
        foreach (var it in Snapshot())
        {
            if (it.M is not { } m) continue;
            long rx = m.Monitor.DataBytesReceived, tx = m.Monitor.DataBytesSent;
            if (rx < it.LastRx) it.LastRx = 0;
            if (tx < it.LastTx) it.LastTx = 0;
            if (rx != it.LastRx || tx != it.LastTx) _dirty = true;
            it.E.Downloaded += rx - it.LastRx;
            it.E.Uploaded += tx - it.LastTx;
            it.LastRx = rx; it.LastTx = tx;
            long rate = IsRunning(m) ? m.Monitor.DownloadRate : 0;
            it.DownAvg = it.DownAvg * 0.8 + rate * 0.2;
        }
    }

    static double Ratio(Entry e)
    {
        long basis = e.Downloaded > 0 ? e.Downloaded : e.Size;
        return basis > 0 ? (double)e.Uploaded / basis : 0;
    }

    void SaveEntries()
    {
        try
        {
            Store.Save(_entriesPath, Snapshot().Select(i => i.E).ToList());
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        catch { }
    }

    void OnNetworkChanged(object? s, EventArgs e) { _netChangedAt = DateTime.UtcNow; _online = NetworkInterface.GetIsNetworkAvailable(); Touch(); }
    void OnNetworkAvailability(object? s, NetworkAvailabilityEventArgs e) { _online = e.IsAvailable; _netChangedAt = DateTime.UtcNow; Touch(); }

    // ================================================================ user actions

    async Task<bool> WithItem(string id, Func<Item, Task> action)
    {
        Item? it;
        lock (_items) _items.TryGetValue(id, out it);
        if (it == null) return false;
        await _gate.WaitAsync();
        try { await action(it); _dirty = true; SaveEntries(); }
        finally { _gate.Release(); }
        await ReconcileAsync();
        Touch();
        return true;
    }

    public Task<bool> PauseAsync(string id) => WithItem(id, async it =>
    {
        it.E.Paused = true;
        it.E.Forced = false;
        if (it.M is { } m && IsRunning(m)) await m.StopAsync(TimeSpan.FromSeconds(5));
    });

    public Task<bool> ResumeAsync(string id, bool force = false) => WithItem(id, it =>
    {
        it.E.Paused = false;
        it.ErrorRetries = 0;
        it.ErrorAt = default;
        if (it.M is { } m && IsDone(m) && it.E.SeedDone) { it.E.SeedDone = false; it.E.Forced = true; }
        if (force) it.E.Forced = true;
        if (it.AttachError != null) return AttachAsync(it, null);
        if (it.M is { State: TorrentState.Error } em) return em.StopAsync(TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    });

    public async Task PauseAllAsync() { foreach (var it in Snapshot()) await PauseAsync(it.E.Id); }

    public async Task ResumeAllAsync()
    {
        foreach (var it in Snapshot()) if (it.E.Paused || it.M?.State == TorrentState.Error) await ResumeAsync(it.E.Id);
    }

    public async Task<bool> RemoveAsync(string id, bool deleteFiles)
    {
        Item? it;
        lock (_items) _items.TryGetValue(id, out it);
        if (it == null) return false;
        await _gate.WaitAsync();
        try
        {
            it.Busy = true;
            lock (_items) _items.Remove(id);
            if (it.M is { } m)
            {
                var files = m.Files.Select(f => f.FullPath).ToList();
                var folder = m.Files.Count > 1 || (m.Torrent?.Files.Count ?? 0) > 1 ? m.ContainingDirectory : null;
                try { await m.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { await _engine.RemoveAsync(m, deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly); } catch { }
                if (deleteFiles)
                {
                    foreach (var f in files) try { if (File.Exists(f)) File.Delete(f); } catch { }
                    if (folder != null && !PathEquals(folder, it.E.SaveDir)) DeleteEmptyDirs(folder);
                }
            }
            try { File.Delete(Path.Combine(_meta, id + ".torrent")); } catch { }
            SaveEntries();
        }
        finally { _gate.Release(); }
        Touch();
        return true;
    }

    static void DeleteEmptyDirs(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir)) DeleteEmptyDirs(d);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    /// <summary>Re-verifies every piece on disk. Runs outside the lock: big torrents take minutes.</summary>
    public async Task<bool> RecheckAsync(string id)
    {
        Item? it;
        lock (_items) _items.TryGetValue(id, out it);
        if (it?.M is not { HasMetadata: true } m || it.Busy) return false;
        it.Busy = true;
        Touch();
        try
        {
            if (IsRunning(m)) await m.StopAsync(TimeSpan.FromSeconds(5));
            await m.HashCheckAsync(false);
            it.WasDone = IsDone(m);
            if (!it.WasDone) { it.E.CompletedAt = null; it.E.SeedDone = false; }
        }
        catch (Exception ex) { it.Blocked = ex.Message; }
        finally { it.Busy = false; }
        await ReconcileAsync();
        Touch();
        return true;
    }

    public Task<bool> SetSequentialAsync(string id, bool on) => WithItem(id, async it =>
    {
        it.E.Sequential = on;
        if (it.M is { HasMetadata: true } m) await ApplyPickerAsync(m, on);
    });

    public Task<bool> SetPriorityAsync(string id, int[] files, string priority) => WithItem(id, async it =>
    {
        if (it.M is not { HasMetadata: true } m) return;
        var p = ToPriority(priority);
        it.E.Priorities ??= new();
        foreach (var i in files)
        {
            if (i < 0 || i >= m.Files.Count) continue;
            await m.SetFilePriorityAsync(m.Files[i], p);
            if (priority == "normal") it.E.Priorities.Remove(i); else it.E.Priorities[i] = priority;
        }
        if (it.E.Priorities.Count == 0) it.E.Priorities = null;
        // Picking more files re-opens a finished torrent for download.
        if (!IsDone(m)) { it.E.SeedDone = false; }
    });

    public Task<bool> MoveOrderAsync(string id, int delta) => WithItem(id, it =>
    {
        var list = Snapshot();
        int idx = list.IndexOf(it);
        int to = delta == int.MinValue ? 0 : delta == int.MaxValue ? list.Count - 1 : Math.Clamp(idx + delta, 0, list.Count - 1);
        list.RemoveAt(idx);
        list.Insert(to, it);
        for (int i = 0; i < list.Count; i++) list[i].E.Order = i;
        return Task.CompletedTask;
    });

    public async Task<bool> MoveAsync(string id, string newDir)
    {
        Item? it;
        lock (_items) _items.TryGetValue(id, out it);
        if (it?.M is not { } m) return false;
        if (PathEquals(newDir, it.E.SaveDir)) return true;
        it.Moving = true;
        Touch();
        try
        {
            Directory.CreateDirectory(newDir);
            if (IsRunning(m)) await m.StopAsync(TimeSpan.FromSeconds(5));
            await m.MoveFilesAsync(newDir, false);
            it.E.SaveDir = newDir;
            _dirty = true;
            return true;
        }
        catch (Exception ex) { it.Blocked = "Move failed: " + ex.Message; return false; }
        finally { it.Moving = false; await ReconcileAsync(); Touch(); }
    }

    static Priority ToPriority(string p) => p switch
    {
        "skip" => Priority.DoNotDownload,
        "low" => Priority.Low,
        "high" => Priority.High,
        _ => Priority.Normal,
    };

    static string FromPriority(Priority p) => p switch
    {
        Priority.DoNotDownload => "skip",
        Priority.Lowest or Priority.Low => "low",
        Priority.High or Priority.Highest or Priority.Immediate => "high",
        _ => "normal",
    };

    // ================================================================ views

    static long Wanted(TorrentManager m) => m.Files.Where(f => f.Priority != Priority.DoNotDownload).Sum(f => f.Length);
    static long DoneBytes(TorrentManager m) =>
        m.Files.Where(f => f.Priority != Priority.DoNotDownload).Sum(f => f.Length == 0 ? 0 : (long)(f.Length * (f.BitField.PercentComplete / 100.0)));

    public IReadOnlyList<TorrentView> Torrents() => Snapshot().Select(ViewOf).Where(v => v != null).ToList()!;

    TorrentView? ViewOf(Item it)
    {
        var e = it.E;
        var m = it.M;
        if (m == null)
            return new TorrentView(e.Id, e.Name, Status.Error, 0, e.Size, e.Size, 0, 0, 0, null, 0, 0, 0, 0, Ratio(e), e.Downloaded, e.Uploaded,
                e.SaveDir, e.AddedAt, e.CompletedAt, e.Sequential, e.Forced, false, it.AttachError, e.Order, 0, null, it.Kind, false);

        bool meta = m.HasMetadata;
        bool done = IsDone(m);
        long wanted = meta ? Wanted(m) : e.Size;
        long have = meta ? DoneBytes(m) : 0;
        bool running = IsRunning(m);
        long down = running ? m.Monitor.DownloadRate : 0, up = running ? m.Monitor.UploadRate : 0;
        int seedsTotal = 0, peersTotal = 0;
        try
        {
            foreach (var tier in m.TrackerManager.Tiers)
                foreach (var s in tier.ScrapeInfo.Values) { seedsTotal = Math.Max(seedsTotal, s.Complete); peersTotal = Math.Max(peersTotal, s.Incomplete); }
        }
        catch { }

        Status st;
        string? err = it.Blocked ?? it.AttachError;
        if (it.Moving) st = Status.Moving;
        else if (m.State == TorrentState.Error) { st = Status.Error; err = m.Error?.Exception?.Message ?? "Disk error."; }
        else if (it.Blocked != null && !e.Paused) st = Status.Error;
        else if (m.State is TorrentState.Hashing or TorrentState.FetchingHashes) st = Status.Checking;
        else if (e.Paused) st = Status.Paused;
        else if (m.State == TorrentState.Metadata) st = Status.Metadata;
        else if (done) st = running ? Status.Seeding : Status.Finished;
        else if (!running) st = Status.Queued;
        else if (it.DownAvg < 1024 && DateTime.UtcNow - it.RunningSince > TimeSpan.FromSeconds(20)) st = Status.Stalled;
        else st = Status.Downloading;

        double progress = meta ? (wanted > 0 ? (double)have / wanted : (done ? 1 : 0)) : 0;
        if (done) progress = 1;
        double? eta = !done && it.DownAvg > 512 && st is Status.Downloading or Status.Stalled ? (wanted - have) / it.DownAvg : null;
        string? open = null;
        if (meta)
        {
            open = m.Files.Count == 1 ? m.Files[0].FullPath : m.ContainingDirectory;
        }

        return new TorrentView(e.Id, e.Name, st, progress, meta ? m.Torrent!.Size : e.Size, wanted, have, down, up, eta,
            m.Peers.Leechs + m.Peers.Seeds, m.Peers.Seeds, Math.Max(peersTotal, m.Peers.Available), seedsTotal,
            Ratio(e), e.Downloaded, e.Uploaded, e.SaveDir, e.AddedAt, e.CompletedAt, e.Sequential, e.Forced, meta, err, e.Order,
            meta ? m.Files.Count : 0, open, it.Kind, meta && m.Torrent!.IsPrivate);
    }

    public async Task<DetailView?> DetailAsync(string id)
    {
        Item? it;
        lock (_items) _items.TryGetValue(id, out it);
        if (it == null) return null;
        var m = it.M;
        var e = it.E;
        if (m == null) return new DetailView(id, id, e.Magnet, null, null, null, 0, 0, [], [], [], []);

        var files = new List<FileView>();
        if (m.HasMetadata)
            for (int i = 0; i < m.Files.Count; i++)
            {
                var f = m.Files[i];
                files.Add(new FileView(i, f.Path, f.Length, f.Length == 0 ? 1 : f.BitField.PercentComplete / 100.0, FromPriority(f.Priority), f.FullPath));
            }

        var peers = new List<PeerView>();
        try
        {
            foreach (var p in await m.GetPeersAsync())
            {
                if (!p.IsConnected) continue;
                var flags = (p.IsSeeder ? "S" : "") + (p.ConnectionDirection == MonoTorrent.Direction.Incoming ? "I" : "") +
                            (p.EncryptionType != EncryptionType.PlainText ? "E" : "") + (p.AmInterested && !p.IsChoking ? "D" : "") + (p.IsInterested && !p.AmChoking ? "U" : "");
                peers.Add(new PeerView(p.Uri.Host + ":" + p.Uri.Port, p.ClientApp.ToString(), p.Monitor.DownloadRate, p.Monitor.UploadRate,
                    p.BitField.PercentComplete / 100.0, p.IsSeeder, p.ConnectionDirection == MonoTorrent.Direction.Incoming, p.EncryptionType != EncryptionType.PlainText, flags));
            }
        }
        catch { }
        peers.Sort((a, b) => (b.Down + b.Up).CompareTo(a.Down + a.Up));

        var trackers = new List<TrackerView>();
        try
        {
            foreach (var tier in m.TrackerManager.Tiers)
                foreach (var t in tier.Trackers)
                {
                    int s = 0, l = 0, d = 0;
                    if (tier.ActiveTracker == t)
                        foreach (var sc in tier.ScrapeInfo.Values) { s = Math.Max(s, sc.Complete); l = Math.Max(l, sc.Incomplete); d = Math.Max(d, sc.Downloaded); }
                    string status = t.Status switch
                    {
                        TrackerState.Ok => "Working",
                        TrackerState.Connecting => "Connecting",
                        TrackerState.Offline => "Offline",
                        TrackerState.InvalidResponse => "Error",
                        _ => tier.ActiveTracker == t ? "Waiting" : "Standby",
                    };
                    trackers.Add(new TrackerView(t.Uri.OriginalString, status, s, l, d, !string.IsNullOrEmpty(t.FailureMessage) ? t.FailureMessage : t.WarningMessage));
                }
        }
        catch { }

        var map = Array.Empty<int>();
        if (m.HasMetadata)
        {
            var bf = m.Bitfield;
            int buckets = Math.Min(bf.Length, 300);
            map = new int[buckets];
            for (int b = 0; b < buckets; b++)
            {
                int start = (int)((long)b * bf.Length / buckets), end = (int)((long)(b + 1) * bf.Length / buckets);
                int c = 0;
                for (int i = start; i < end; i++) if (bf[i]) c++;
                map[b] = end > start ? c * 100 / (end - start) : 0;
            }
        }

        var tt = m.Torrent;
        return new DetailView(id, m.InfoHashes.V1OrV2.ToHex().ToLowerInvariant(), e.Magnet, tt?.Comment, tt?.CreatedBy,
            tt != null && tt.CreationDate.Year > 1971 ? tt.CreationDate : null, tt?.PieceLength ?? 0, tt?.PieceCount ?? 0, files, peers, trackers, map);
    }

    public GlobalView Global()
    {
        long down = 0, up = 0;
        try { down = _engine.TotalDownloadRate; up = _engine.TotalUploadRate; } catch { }
        int nodes = 0; string dht = "Off";
        try { if (Settings.Dht && _engine.Dht != null) { nodes = _engine.Dht.NodeCount; dht = _engine.Dht.State.ToString(); } } catch { }
        string port;
        try
        {
            var map = _engine.PortMappings;
            if (_sawIncoming) port = "Open";
            else if (!Settings.PortForwarding) port = "Closed";
            else if (map.Created.Count > 0) port = "Forwarded";
            else if (map.Pending.Count > 0) port = "Checking";
            else port = "Closed";
        }
        catch { port = "Checking"; }
        long free = -1;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Settings.SaveDir))!).AvailableFreeSpace; } catch { }
        int conns = 0, half = 0;
        try { conns = _engine.ConnectionManager.OpenConnections; half = _engine.ConnectionManager.HalfOpenConnections; } catch { }
        return new GlobalView(down, up, conns, half, nodes, dht, Settings.Port, port, free, _online);
    }

    /// <summary>Connects to peers the user knows about (ip:port, [ipv6]:port).</summary>
    public async Task<int> AddPeersAsync(string id, IEnumerable<string> addresses)
    {
        Item? it;
        lock (_items) _items.TryGetValue(id, out it);
        if (it?.M is not { } m) return 0;
        var peers = new List<PeerInfo>();
        foreach (var a in addresses)
        {
            var text = a.Trim();
            if (!IPEndPoint.TryParse(text, out var ep) || ep.Port == 0) continue;
            var scheme = ep.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
            peers.Add(new PeerInfo(new Uri($"{scheme}://{ep}")));
        }
        if (peers.Count == 0) return 0;
        return await m.AddPeersAsync(peers);
    }

    public string? MagnetOf(string id) { lock (_items) return _items.TryGetValue(id, out var it) ? it.E.Magnet : null; }

    // ================================================================ helpers

    static readonly Dictionary<string, string> Kinds = BuildKinds();
    static Dictionary<string, string> BuildKinds()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string kind, string exts) { foreach (var x in exts.Split(' ')) d[x] = kind; }
        Add("video", "mp4 mkv mov avi wmv flv webm m4v 3gp ts m2ts mpeg mpg vob");
        Add("music", "mp3 m4a aac flac wav ogg opus wma ape alac");
        Add("image", "jpg jpeg png gif webp heic bmp tif tiff psd raw");
        Add("archive", "zip rar 7z tar gz tgz bz2 xz zst cab");
        Add("disc", "iso img dmg bin cue nrg vhd vhdx vmdk");
        Add("app", "exe msi apk appx msix deb rpm pkg");
        Add("doc", "pdf epub mobi azw3 cbz cbr doc docx txt");
        return d;
    }

    internal static string KindOf(Torrent t)
    {
        var big = t.Files.OrderByDescending(f => f.Length).FirstOrDefault();
        if (big == null) return "file";
        var ext = Path.GetExtension(big.Path).TrimStart('.');
        if (ext.Length > 0 && Kinds.TryGetValue(ext, out var k)) return k;
        return t.Files.Count > 1 ? "folder" : "file";
    }
}
