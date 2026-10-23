using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Net;
using System.Text.Json.Nodes;

namespace ClipFx.Share;

public sealed record SourceItem(ItemInfo Info, string? LocalPath);

public sealed record SendOptions(int Streams, int ChunkBytes, bool Verify);

/// <summary>A transfer being sent to one device. Survives disconnects by re-offering and resuming.</summary>
public sealed class OutgoingSession
{
    public const int SmallFile = 1024 * 1024;
    static readonly bool Debug = Environment.GetEnvironmentVariable("CFS_DEBUG") == "1";
    const int MaxPartsPerBatch = 128;
    static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);

    public sealed class Saved
    {
        public string Id { get; set; } = "";
        public DeviceInfo Target { get; set; } = new("", "", "", 0);
        public string? Address { get; set; }
        public List<ItemInfo> Items { get; set; } = [];
        public List<string?> Paths { get; set; } = [];
        public List<long[][]> Acked { get; set; } = [];
        public TransferState State { get; set; }
        public DateTime Started { get; set; }
        public DateTime? Finished { get; set; }
        public string? Error { get; set; }
    }

    sealed record Part(int I, long Off, long Len);
    sealed record Work(Part[] Parts) { public long Bytes => Parts.Sum(p => p.Len); }

    sealed class PeerPausedException : Exception { }
    sealed class PeerCancelledException : Exception { }
    sealed class UnknownSessionException : Exception { }

    readonly object _gate = new();
    readonly RangeSet[] _acked;
    readonly string?[] _paths;
    readonly Func<DeviceInfo> _self;
    readonly Func<string, IPEndPoint?> _resolve;
    readonly Func<SendOptions> _options;
    CancellationTokenSource? _run;
    Task? _task;
    long _live;
    string? _key;

    public string Id { get; }
    public DeviceInfo Target { get; }
    public IPEndPoint? LastEndpoint { get; private set; }
    public IReadOnlyList<ItemInfo> Items { get; }
    public TransferState State { get; private set; }
    public DateTime Started { get; private set; }
    public DateTime? Finished { get; private set; }
    public string? Error { get; private set; }
    public long TotalBytes { get; }
    public RateMeter Rate { get; } = new();
    public string? CurrentFile { get; private set; }
    public event Action? Changed;
    /// <summary>Lets a platform route the socket (e.g. bind it to Wi-Fi).</summary>
    public Func<System.Net.Sockets.Socket, Task>? PrepareSocket { get; set; }

    public OutgoingSession(string id, DeviceInfo target, IPEndPoint? endpoint, IReadOnlyList<SourceItem> items,
        Func<DeviceInfo> self, Func<string, IPEndPoint?> resolve, Func<SendOptions> options)
    {
        Id = id; Target = target; LastEndpoint = endpoint;
        Items = items.Select(x => x.Info).ToList();
        _paths = items.Select(x => x.LocalPath).ToArray();
        _acked = Items.Select(_ => new RangeSet()).ToArray();
        TotalBytes = Items.Where(i => i.Type == ItemType.File).Sum(i => i.Size);
        _self = self; _resolve = resolve; _options = options;
        Started = DateTime.UtcNow;
        State = TransferState.Connecting;
    }

    public static OutgoingSession FromSaved(Saved s, Func<DeviceInfo> self, Func<string, IPEndPoint?> resolve, Func<SendOptions> options)
    {
        IPEndPoint? ep = s.Address != null && IPEndPoint.TryParse(s.Address, out var e) ? e : null;
        var items = s.Items.Select((it, k) => new SourceItem(it, k < s.Paths.Count ? s.Paths[k] : null)).ToList();
        var o = new OutgoingSession(s.Id, s.Target, ep, items, self, resolve, options)
        {
            Started = s.Started, Finished = s.Finished, Error = s.Error,
        };
        for (int i = 0; i < o._acked.Length && i < s.Acked.Count; i++) foreach (var r in s.Acked[i]) o._acked[i].Add(r[0], r[1]);
        o._live = o.Done;
        o.State = States.IsFinal(s.State) ? s.State : TransferState.Interrupted;
        return o;
    }

    public Saved ToSaved()
    {
        lock (_gate)
            return new Saved
            {
                Id = Id, Target = Target, Address = LastEndpoint?.ToString(), Items = [.. Items], Paths = [.. _paths],
                Acked = _acked.Select(a => a.ToArray()).ToList(), State = State, Started = Started, Finished = Finished, Error = Error,
            };
    }

    public long Done => Items.Where(i => i.Type == ItemType.File).Sum(i => _acked[i.I].Covered);
    public long Live => Math.Min(TotalBytes, Interlocked.Read(ref _live));

    void SetState(TransferState s, string? error = null)
    {
        lock (_gate)
        {
            if (State == s && Error == error) return;
            State = s;
            Error = error;
            if (States.IsFinal(s)) Finished ??= DateTime.UtcNow;
            if (s == TransferState.Completed) Error = null;
        }
        Changed?.Invoke();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (States.IsFinal(State)) return;
            if (_run is { IsCancellationRequested: false } && _task is { IsCompleted: false }) return; // already running
            _run = new CancellationTokenSource();
            var ct = _run.Token;
            // A paused run may still be winding down; start after it has fully stopped.
            var previous = _task ?? Task.CompletedTask;
            _task = previous.ContinueWith(_ => RunAsync(ct), TaskScheduler.Default).Unwrap();
        }
    }

    public void Pause()
    {
        if (States.IsFinal(State)) return;
        _run?.Cancel();
        SetState(TransferState.Paused);
        _ = NotifyPeerAsync("pause");
    }

    public void Resume()
    {
        if (States.IsFinal(State)) return;
        SetState(TransferState.Connecting);
        Start();
    }

    public void Cancel()
    {
        if (States.IsFinal(State)) return;
        _run?.Cancel();
        SetState(TransferState.Cancelled);
        _ = NotifyPeerAsync("cancel");
    }

    /// <summary>For a finished-with-error transfer: try again from where it stopped.</summary>
    public void Retry()
    {
        lock (_gate)
        {
            if (State is not (TransferState.Failed or TransferState.Interrupted or TransferState.Declined)) return;
            State = TransferState.Connecting; Finished = null; Error = null;
        }
        Changed?.Invoke();
        Start();
    }

    async Task NotifyPeerAsync(string op)
    {
        if (_key is null) return;
        try
        {
            var ep = Endpoint();
            if (ep is null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await using var c = await Conn.ConnectAsync(ep.Address, ep.Port, TimeSpan.FromSeconds(3), cts.Token, PrepareSocket);
            await c.WriteMessageAsync(new JsonObject { ["op"] = op, ["session"] = Id, ["key"] = _key }, cts.Token);
            await c.ReadMessageAsync(cts.Token);
        }
        catch { }
    }

    IPEndPoint? Endpoint()
    {
        var ep = _resolve(Target.Id) ?? LastEndpoint;
        if (ep != null) LastEndpoint = ep;
        return ep;
    }

    async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        var since = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ep = Endpoint() ?? throw new IOException($"{Target.Name} isn't on this network.");
                if (State != TransferState.Waiting) SetState(attempt == 0 ? TransferState.Connecting : TransferState.Reconnecting, State == TransferState.Reconnecting ? Error : null);
                var have = await OfferAsync(ep, ct);
                if (have is null) return; // declined
                attempt = 0;
                since = DateTime.UtcNow;
                SetState(TransferState.Active);
                await SendAllAsync(ep, ct);
                if (await FinishAsync(ep, ct))
                {
                    CurrentFile = null;
                    SetState(TransferState.Completed);
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (PeerCancelledException) { SetState(TransferState.Cancelled, "Cancelled by the receiver."); return; }
            catch (SourceException ex) { SetState(TransferState.Failed, ex.Message); return; }
            catch (Exception) when (ct.IsCancellationRequested) { return; } // paused or cancelled meanwhile
            catch (Exception ex)
            {
                if (Debug) Console.Error.WriteLine("sender: " + ex);
                attempt++;
                if (DateTime.UtcNow - since > GiveUpAfter) { SetState(TransferState.Interrupted, "Connection lost."); return; }
                SetState(TransferState.Reconnecting, Friendly(ex));
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 0.5 * Math.Pow(2, Math.Min(attempt, 5)))), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    static string Friendly(Exception ex) => ex switch
    {
        TimeoutException => "No response from the other device.",
        System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused => "The other device isn't accepting connections.",
        System.Net.Sockets.SocketException => "Network unavailable.",
        IOException io => io.Message,
        _ => ex.Message,
    };

    /// <summary>Offers (or re-offers) the transfer. Null when declined.</summary>
    async Task<bool?> OfferAsync(IPEndPoint ep, CancellationToken ct)
    {
        await using var c = await Conn.ConnectAsync(ep.Address, ep.Port, TimeSpan.FromSeconds(5), ct, PrepareSocket);
        var self = _self();
        var msg = new JsonObject
        {
            ["op"] = "offer", ["v"] = Proto.Version, ["session"] = Id,
            ["from"] = Json.From(self),
            ["items"] = Json.From(Items),
            ["totalSize"] = TotalBytes,
        };
        await c.WriteMessageAsync(msg, ct);
        if (_key is null) SetState(TransferState.Waiting);
        // The receiver may take a while to decide; it sends keep-alives meanwhile.
        var reply = await c.ReadMessageAsync(ct, TimeSpan.FromSeconds(30)) ?? throw new IOException("Connection closed.");
        if (!reply.Bool("ok")) throw new IOException(reply.Str("error") ?? "Offer failed.");
        if (!reply.Bool("accepted"))
        {
            var reason = reply.Str("reason");
            if (reason == "cancelled") throw new PeerCancelledException();
            SetState(TransferState.Declined, reason switch
            {
                "space" => "Not enough space on the other device.",
                "timeout" => "No answer from the other device.",
                "busy" => "The other device is busy.",
                _ => "Declined.",
            });
            return null;
        }
        _key = reply.Str("key");
        if (reply["have"] is JsonArray have)
            foreach (var h in have.OfType<JsonObject>())
            {
                int i = (int)h.Long("i");
                if (i >= 0 && i < _acked.Length && h["r"] is JsonArray r)
                    foreach (var range in r.OfType<JsonArray>()) _acked[i].Add((long)range[0]!, (long)range[1]!);
            }
        Interlocked.Exchange(ref _live, Done);
        return true;
    }

    List<Work> Plan(int chunk)
    {
        var works = new List<Work>();
        var batch = new List<Part>();
        long batchBytes = 0;
        void Flush() { if (batch.Count > 0) { works.Add(new Work([.. batch])); batch.Clear(); batchBytes = 0; } }

        foreach (var it in Items.Where(i => i.Type == ItemType.File))
        {
            foreach (var (s, e) in _acked[it.I].Missing(it.Size))
            {
                if (it.Size <= SmallFile)
                {
                    // Small files travel together so each one doesn't cost a round trip.
                    if (batchBytes + (e - s) > chunk || batch.Count >= MaxPartsPerBatch) Flush();
                    batch.Add(new Part(it.I, s, e - s));
                    batchBytes += e - s;
                    continue;
                }
                for (long off = s; off < e; off += chunk)
                    works.Add(new Work([new Part(it.I, off, Math.Min(chunk, e - off))]));
            }
        }
        Flush();
        return works;
    }

    async Task SendAllAsync(IPEndPoint ep, CancellationToken ct)
    {
        var opt = _options();
        var queue = new ConcurrentQueue<Work>(Plan(Math.Max(256 * 1024, opt.ChunkBytes)));
        if (queue.IsEmpty) return;
        int streams = Math.Clamp(opt.Streams, 1, 16);
        streams = Math.Min(streams, queue.Count);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, streams).Select(_ => Task.Run(() => WorkerAsync(ep, queue, opt.Verify, linked.Token))).ToList();
        try
        {
            while (workers.Count > 0)
            {
                var t = await Task.WhenAny(workers);
                workers.Remove(t);
                if (t.IsFaulted || t.IsCanceled)
                {
                    linked.Cancel();
                    await Task.WhenAll(workers.Select(w => w.ContinueWith(_ => { })));
                    ct.ThrowIfCancellationRequested();
                    if (t.IsCanceled) throw new TimeoutException("Connection timed out.");
                    await t;
                }
            }
        }
        finally { linked.Cancel(); }
        ct.ThrowIfCancellationRequested();
        if (!queue.IsEmpty) throw new IOException("Transfer stopped.");
    }

    async Task WorkerAsync(IPEndPoint ep, ConcurrentQueue<Work> queue, bool verify, CancellationToken ct)
    {
        var bufA = new byte[1024 * 1024];
        var bufB = new byte[1024 * 1024];
        Conn? c = null;
        try
        {
            while (queue.TryDequeue(out var w))
            {
                if (ct.IsCancellationRequested) { queue.Enqueue(w); return; }
                try
                {
                    c ??= await Conn.ConnectAsync(ep.Address, ep.Port, TimeSpan.FromSeconds(5), ct, PrepareSocket);
                    await SendWorkAsync(c, w, verify, bufA, bufB, ct);
                }
                catch (PeerPausedException)
                {
                    queue.Enqueue(w);
                    await WaitWhilePeerPausedAsync(c!, ct);
                }
                catch (Exception ex)
                {
                    if (Debug) Console.Error.WriteLine("worker: " + ex.GetType().Name + " " + ex.Message);
                    queue.Enqueue(w);
                    throw;
                }
            }
        }
        finally
        {
            if (c != null) await c.DisposeAsync();
        }
    }

    async Task SendWorkAsync(Conn c, Work w, bool verify, byte[] bufA, byte[] bufB, CancellationToken ct)
    {
        var parts = new JsonArray(w.Parts.Select(p => (JsonNode)new JsonObject { ["i"] = p.I, ["off"] = p.Off, ["len"] = p.Len }).ToArray());
        await c.WriteMessageAsync(new JsonObject { ["op"] = "data", ["session"] = Id, ["key"] = _key, ["parts"] = parts }, ct);
        long sentHere = 0;
        try
        {
            foreach (var p in w.Parts)
            {
                CurrentFile = Items[p.I].Path;
                sentHere += await SendPartAsync(c, p, verify, bufA, bufB, ct);
            }
            var reply = await c.ReadMessageAsync(ct) ?? throw new IOException("Connection closed.");
            if (!reply.Bool("ok"))
            {
                Interlocked.Add(ref _live, -sentHere);
                switch (reply.Str("error"))
                {
                    case "paused": throw new PeerPausedException();
                    case "cancelled": throw new PeerCancelledException();
                    case "unknown": throw new UnknownSessionException();
                    case "crc": throw new IOException("Data was damaged in transit; resending.");
                    default: throw new IOException(reply.Str("error") ?? "Receiver error.");
                }
            }
        }
        catch (Exception ex) when (ex is not PeerPausedException and not PeerCancelledException and not UnknownSessionException)
        {
            if (!(ex is IOException { Message: "Data was damaged in transit; resending." })) Interlocked.Add(ref _live, -sentHere);
            throw;
        }
        foreach (var p in w.Parts) _acked[p.I].Add(p.Off, p.Off + p.Len);
        if (State == TransferState.PausedByPeer) SetState(TransferState.Active);
    }

    async Task<long> SendPartAsync(Conn c, Part p, bool verify, byte[] bufA, byte[] bufB, CancellationToken ct)
    {
        var path = _paths[p.I] ?? throw new SourceException("A file to send is missing.");
        Microsoft.Win32.SafeHandles.SafeFileHandle h;
        try { h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw new SourceException($"Can't read {Path.GetFileName(path)}: {ex.Message}");
        }
        using (h)
        {
            if (RandomAccess.GetLength(h) < p.Off + p.Len) throw new SourceException($"{Path.GetFileName(path)} changed while sending.");
            var crc = verify ? new Crc32() : null;
            long pos = 0;
            Task pending = Task.CompletedTask;
            bool useA = true;
            while (pos < p.Len)
            {
                var buf = useA ? bufA : bufB;
                int n = (int)Math.Min(buf.Length, p.Len - pos);
                int read = 0;
                while (read < n)
                {
                    int r = await RandomAccess.ReadAsync(h, buf.AsMemory(read, n - read), p.Off + pos + read, ct);
                    if (r == 0) throw new SourceException($"{Path.GetFileName(path)} changed while sending.");
                    read += r;
                }
                crc?.Append(buf.AsSpan(0, n));
                await pending;
                pending = c.WriteAsync(buf.AsMemory(0, n), ct);
                pos += n;
                Interlocked.Add(ref _live, n);
                Rate.Add(n);
                useA = !useA;
            }
            await pending;
            await c.WriteUInt32Async(crc?.GetCurrentHashAsUInt32() ?? 0, ct);
            return p.Len;
        }
    }

    async Task WaitWhilePeerPausedAsync(Conn c, CancellationToken ct)
    {
        SetState(TransferState.PausedByPeer);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            await c.WriteMessageAsync(new JsonObject { ["op"] = "status", ["session"] = Id, ["key"] = _key }, ct);
            var r = await c.ReadMessageAsync(ct) ?? throw new IOException("Connection closed.");
            var st = r.Str("state");
            if (st == "cancelled") throw new PeerCancelledException();
            if (st != "paused") { SetState(TransferState.Active); return; }
        }
    }

    async Task<bool> FinishAsync(IPEndPoint ep, CancellationToken ct)
    {
        await using var c = await Conn.ConnectAsync(ep.Address, ep.Port, TimeSpan.FromSeconds(5), ct, PrepareSocket);
        await c.WriteMessageAsync(new JsonObject { ["op"] = "done", ["session"] = Id, ["key"] = _key }, ct);
        var r = await c.ReadMessageAsync(ct, TimeSpan.FromMinutes(2)) ?? throw new IOException("Connection closed.");
        if (r.Bool("ok")) return true;
        if (r.Str("error") == "cancelled") throw new PeerCancelledException();
        if (r.Str("error") == "incomplete") return false; // loop re-offers and sends what's missing
        throw new IOException(r.Str("error") ?? "Finishing failed.");
    }
}

public sealed class SourceException(string message) : Exception(message);
