using System.IO.Hashing;
using System.Net;
using Microsoft.Win32.SafeHandles;

namespace ClipFx.Share;

/// <summary>A transfer being received. Data lands in "*.cfpart" files and is renamed when complete.</summary>
public sealed class IncomingSession
{
    public const string PartSuffix = ".cfpart";
    const int MaxOpenFiles = 64;
    /// <summary>Benchmarks only: measure the network path without the disk.</summary>
    internal static bool NoWrite = Environment.GetEnvironmentVariable("CFS_BENCH_NOWRITE") == "1";

    public sealed class Saved
    {
        public string Id { get; set; } = "";
        public DeviceInfo From { get; set; } = new("", "", "", 0);
        public string? FromAddress { get; set; }
        public List<ItemInfo> Items { get; set; } = [];
        public string Key { get; set; } = "";
        public string Root { get; set; } = "";
        public List<long[][]> Have { get; set; } = [];
        public List<string?> Finals { get; set; } = [];
        public List<bool> Skipped { get; set; } = [];
        public TransferState State { get; set; }
        public DateTime Started { get; set; }
        public DateTime? Finished { get; set; }
        public string? Error { get; set; }
        public string Conflict { get; set; } = "rename";
    }

    readonly object _gate = new();
    readonly Dictionary<int, SafeFileHandle> _open = new();
    readonly LinkedList<int> _lru = new();
    readonly RangeSet[] _have;
    readonly string?[] _finals;
    readonly bool[] _skipped;
    long _live; // bytes written including parts still in flight

    public string Id { get; }
    public DeviceInfo From { get; }
    public IPAddress? FromAddress { get; set; }
    public IReadOnlyList<ItemInfo> Items { get; }
    public string Key { get; }
    public string Root { get; private set; }
    public string Conflict { get; private set; }
    public TransferState State { get; private set; }
    public DateTime Started { get; }
    public DateTime? Finished { get; private set; }
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    public string? Error { get; private set; }
    public long TotalBytes { get; }
    public RateMeter Rate { get; } = new();
    public event Action? Changed;

    public IncomingSession(string id, DeviceInfo from, IPAddress? fromAddress, IReadOnlyList<ItemInfo> items, string key)
    {
        Id = id; From = from; FromAddress = fromAddress; Items = items; Key = key;
        Root = ""; Conflict = "rename";
        _have = items.Select(_ => new RangeSet()).ToArray();
        _finals = new string?[items.Count];
        _skipped = new bool[items.Count];
        TotalBytes = items.Where(i => i.Type == ItemType.File).Sum(i => i.Size);
        Started = DateTime.UtcNow;
        State = TransferState.Waiting;
    }

    IncomingSession(Saved s) : this(s.Id, s.From, s.FromAddress is null ? null : IPAddress.Parse(s.FromAddress), s.Items, s.Key)
    {
        Root = s.Root; Conflict = s.Conflict; State = s.State; Started = s.Started; Finished = s.Finished; Error = s.Error;
        for (int i = 0; i < _have.Length && i < s.Have.Count; i++) foreach (var r in s.Have[i]) _have[i].Add(r[0], r[1]);
        for (int i = 0; i < _finals.Length && i < s.Finals.Count; i++) _finals[i] = s.Finals[i];
        for (int i = 0; i < _skipped.Length && i < s.Skipped.Count; i++) _skipped[i] = s.Skipped[i];
        _live = Done;
        // A restart interrupts whatever was in progress; the sender resumes it.
        if (!States.IsFinal(State) && State != TransferState.Waiting) State = TransferState.Interrupted;
    }

    public static IncomingSession FromSaved(Saved s) => new(s);

    public Saved ToSaved()
    {
        lock (_gate)
            return new Saved
            {
                Id = Id, From = From, FromAddress = FromAddress?.ToString(), Items = [.. Items], Key = Key, Root = Root,
                Have = _have.Select(h => h.ToArray()).ToList(), Finals = [.. _finals], Skipped = [.. _skipped],
                State = State, Started = Started, Finished = Finished, Error = Error, Conflict = Conflict,
            };
    }

    public long Done => Items.Where(i => i.Type == ItemType.File).Sum(i => _have[i.I].Covered);
    public long Live => Interlocked.Read(ref _live);
    public bool IsComplete => Items.All(i => i.Type != ItemType.File || _have[i.I].IsComplete(i.Size));
    public string? FinalPath(int i) => _finals[i];
    public bool WasSkipped(int i) => _skipped[i];

    void Notify() => Changed?.Invoke();

    public void SetState(TransferState s, string? error = null)
    {
        lock (_gate)
        {
            if (State == s && error == Error) return;
            State = s;
            if (error != null) Error = error;
            if (States.IsFinal(s)) Finished ??= DateTime.UtcNow;
        }
        Notify();
    }

    /// <summary>Accepts into a save folder. With "skip", files that already exist are marked done so they are never sent.</summary>
    public void Accept(string root, string conflict)
    {
        lock (_gate)
        {
            Root = root;
            Conflict = conflict;
            if (conflict == "skip")
            {
                foreach (var it in Items.Where(i => i.Type == ItemType.File))
                {
                    var final = PathSafety.Combine(root, it.Path);
                    if (File.Exists(final)) { _skipped[it.I] = true; _have[it.I].Add(0, it.Size); _finals[it.I] = final; }
                }
                _live = Done;
            }
            State = TransferState.Active;
        }
        Notify();
    }

    public IEnumerable<object> HaveForWire() =>
        Items.Where(i => i.Type == ItemType.File && _have[i.I].Covered > 0)
             .Select(i => new { i = i.I, r = _have[i.I].ToArray() });

    string PartPath(ItemInfo it) => PathSafety.Combine(Root, it.Path) + PartSuffix;

    SafeFileHandle Handle(ItemInfo it)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(it.I, out var h)) { _lru.Remove(it.I); _lru.AddFirst(it.I); return h; }
            var path = PartPath(it);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool exists = File.Exists(path);
            h = exists
                ? File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous)
                : File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous, preallocationSize: it.Size);
            if (!exists || RandomAccess.GetLength(h) != it.Size) RandomAccess.SetLength(h, it.Size);
            if (OperatingSystem.IsWindows() && !exists) try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); } catch { }
            _open[it.I] = h;
            _lru.AddFirst(it.I);
            while (_open.Count > MaxOpenFiles)
            {
                int old = _lru.Last!.Value;
                _lru.RemoveLast();
                _open.Remove(old, out var oh);
                oh?.Dispose();
            }
            return h;
        }
    }

    /// <summary>
    /// Reads one part from the connection into the file, with double buffering so the
    /// network and the disk work at the same time. Returns false when the CRC doesn't match.
    /// </summary>
    public async Task<bool> ReceivePartAsync(Conn conn, int index, long off, long len, bool write, byte[] bufA, byte[] bufB, CancellationToken ct)
    {
        if (index < 0 || index >= Items.Count || Items[index].Type != ItemType.File) throw new IOException("Bad item.");
        var it = Items[index];
        if (off < 0 || len < 0 || off + len > it.Size) throw new IOException("Bad range.");
        var h = write ? Handle(it) : null;
        var crc = new Crc32();
        long pos = 0;
        Task pending = Task.CompletedTask;
        bool useA = true;
        while (pos < len)
        {
            var buf = useA ? bufA : bufB;
            int n = (int)Math.Min(buf.Length, len - pos);
            await conn.ReadExactAsync(buf.AsMemory(0, n), ct);
            crc.Append(buf.AsSpan(0, n));
            await pending;
            if (h != null)
            {
                long at = off + pos;
                pending = NoWrite ? Task.CompletedTask : RandomAccess.WriteAsync(h, buf.AsMemory(0, n), at, ct).AsTask();
                Interlocked.Add(ref _live, n);
                Rate.Add(n);
            }
            pos += n;
            useA = !useA;
            LastActivity = DateTime.UtcNow;
        }
        await pending;
        uint expected = await conn.ReadUInt32Async(ct);
        if (h == null) return true;
        if (expected != 0 && expected != crc.GetCurrentHashAsUInt32())
        {
            Interlocked.Add(ref _live, -len);
            return false;
        }
        _have[index].Add(off, off + len);
        return true;
    }

    /// <summary>Renames finished parts into place, creates folders, applies times. Returns missing items, if any.</summary>
    public List<int> Finish()
    {
        var missing = Items.Where(i => i.Type == ItemType.File && !_have[i.I].IsComplete(i.Size)).Select(i => i.I).ToList();
        if (missing.Count > 0) return missing;
        CloseFiles();
        foreach (var it in Items)
        {
            switch (it.Type)
            {
                case ItemType.Dir:
                    Directory.CreateDirectory(PathSafety.Combine(Root, it.Path));
                    break;
                case ItemType.File when !_skipped[it.I] && _finals[it.I] is null:
                {
                    var final = PathSafety.Combine(Root, it.Path);
                    var part = final + PartSuffix;
                    Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                    if (!File.Exists(part))
                    {
                        // Empty files never got a part file.
                        if (it.Size == 0) File.WriteAllBytes(part, []);
                        else throw new IOException($"Missing data for {it.Path}.");
                    }
                    if (File.Exists(final))
                    {
                        if (Conflict == "overwrite") File.Delete(final);
                        else final = PathSafety.Unique(final);
                    }
                    File.Move(part, final);
                    if (OperatingSystem.IsWindows()) try { File.SetAttributes(final, File.GetAttributes(final) & ~FileAttributes.Hidden); } catch { }
                    if (it.Mtime > 0) try { File.SetLastWriteTimeUtc(final, DateTimeOffset.FromUnixTimeMilliseconds(it.Mtime).UtcDateTime); } catch { }
                    _finals[it.I] = final;
                    break;
                }
            }
        }
        SetState(TransferState.Completed);
        return [];
    }

    public void CloseFiles()
    {
        lock (_gate)
        {
            foreach (var h in _open.Values) h.Dispose();
            _open.Clear();
            _lru.Clear();
        }
    }

    /// <summary>Removes partial data.</summary>
    public void Discard()
    {
        CloseFiles();
        if (string.IsNullOrEmpty(Root)) return;
        foreach (var it in Items.Where(i => i.Type == ItemType.File && _finals[i.I] is null))
            try { File.Delete(PartPath(it)); } catch { }
    }

    /// <summary>Where to point "Open folder": the save root, or the top folder of the transfer.</summary>
    public string? OpenTarget()
    {
        if (string.IsNullOrEmpty(Root)) return null;
        var firstSeg = Items.Select(i => PathSafety.Segments(i.Path)).Where(s => s.Length > 1).Select(s => s[0]).Distinct().ToList();
        if (firstSeg.Count == 1 && Items.All(i => PathSafety.Segments(i.Path)[0] == firstSeg[0])) return Path.Combine(Root, firstSeg[0]);
        return Root;
    }
}

/// <summary>Bytes per second over a sliding window.</summary>
public sealed class RateMeter
{
    readonly object _gate = new();
    readonly Queue<(long T, long N)> _samples = new();
    long _sum;
    const long Window = 2000;

    public void Add(long n)
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            _samples.Enqueue((now, n));
            _sum += n;
            Trim(now);
        }
    }

    void Trim(long now)
    {
        while (_samples.Count > 0 && now - _samples.Peek().T > Window) _sum -= _samples.Dequeue().N;
    }

    public double BytesPerSecond
    {
        get
        {
            long now = Environment.TickCount64;
            lock (_gate)
            {
                Trim(now);
                if (_samples.Count == 0) return 0;
                double span = Math.Max(500, now - _samples.Peek().T);
                return _sum * 1000.0 / span;
            }
        }
    }
}
