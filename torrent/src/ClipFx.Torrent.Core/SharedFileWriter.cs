using Microsoft.Win32.SafeHandles;
using MonoTorrent;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace ClipFx.Torrents;

/// <summary>
/// Disk access that never locks other programs out: files are opened with full sharing, and write
/// access is only held while pieces are being written. A finished file can be played, copied or
/// hashed while it seeds.
/// </summary>
sealed class SharedFileWriter : IPieceWriter
{
    sealed class Slot
    {
        public required SafeFileHandle Handle;
        public bool Writable;
        public long LastUse;
    }

    readonly Dictionary<string, Slot> _open = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();
    static readonly long IdleWriteTicks = TimeSpan.FromSeconds(4).Ticks;

    public SharedFileWriter(int maximumOpenFiles) => MaximumOpenFiles = Math.Max(1, maximumOpenFiles);

    public int MaximumOpenFiles { get; private set; }
    public int OpenFiles { get { lock (_gate) return _open.Count; } }

    const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

    Slot Get(string path, bool write)
    {
        long now = DateTime.UtcNow.Ticks;
        if (_open.TryGetValue(path, out var s))
        {
            if (!write || s.Writable) { s.LastUse = now; return s; }
            s.Handle.Dispose();
            _open.Remove(path);
        }
        Evict(now);
        SafeFileHandle h;
        if (write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            h = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, Share);
        }
        else h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, Share);
        s = new Slot { Handle = h, Writable = write, LastUse = now };
        _open[path] = s;
        return s;
    }

    void Evict(long now)
    {
        // Drop write access that's gone quiet, then the least recently used files if we're over the limit.
        foreach (var kv in _open.Where(kv => kv.Value.Writable && now - kv.Value.LastUse > IdleWriteTicks).ToList())
        {
            kv.Value.Handle.Dispose();
            _open.Remove(kv.Key);
        }
        while (_open.Count >= MaximumOpenFiles)
        {
            var oldest = _open.MinBy(kv => kv.Value.LastUse);
            oldest.Value.Handle.Dispose();
            _open.Remove(oldest.Key);
        }
    }

    public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer)
    {
        lock (_gate)
        {
            if (!File.Exists(file.FullPath)) return ReusableTask.FromResult(0);
            var s = Get(file.FullPath, false);
            long len = RandomAccess.GetLength(s.Handle);
            if (offset >= len) return ReusableTask.FromResult(0);
            int want = (int)Math.Min(buffer.Length, len - offset);
            int total = 0;
            while (total < want)
            {
                int n = RandomAccess.Read(s.Handle, buffer.Span.Slice(total, want - total), offset + total);
                if (n <= 0) break;
                total += n;
            }
            return ReusableTask.FromResult(total);
        }
    }

    public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
    {
        lock (_gate)
        {
            var s = Get(file.FullPath, true);
            RandomAccess.Write(s.Handle, buffer.Span, offset);
        }
        return ReusableTask.CompletedTask;
    }

    public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file) => ReusableTask.FromResult(File.Exists(file.FullPath));

    public ReusableTask FlushAsync(ITorrentManagerFile file)
    {
        lock (_gate)
            if (_open.TryGetValue(file.FullPath, out var s) && s.Writable) RandomAccess.FlushToDisk(s.Handle);
        return ReusableTask.CompletedTask;
    }

    public ReusableTask CloseAsync(ITorrentManagerFile file)
    {
        lock (_gate) Close(file.FullPath);
        return ReusableTask.CompletedTask;
    }

    void Close(string path)
    {
        if (_open.Remove(path, out var s)) s.Handle.Dispose();
    }

    /// <summary>Gives up write access to these files (called when a download finishes).</summary>
    public void Release(IEnumerable<string> paths)
    {
        lock (_gate) foreach (var p in paths) if (_open.TryGetValue(p, out var s) && s.Writable) Close(p);
    }

    public ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
    {
        lock (_gate)
        {
            Close(file.FullPath);
            Close(fullPath);
            if (File.Exists(file.FullPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.Move(file.FullPath, fullPath, overwrite);
            }
        }
        return ReusableTask.CompletedTask;
    }

    public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles)
    {
        lock (_gate)
        {
            MaximumOpenFiles = Math.Max(1, maximumOpenFiles);
            Evict(DateTime.UtcNow.Ticks);
        }
        return ReusableTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _open.Values) s.Handle.Dispose();
            _open.Clear();
        }
    }
}
