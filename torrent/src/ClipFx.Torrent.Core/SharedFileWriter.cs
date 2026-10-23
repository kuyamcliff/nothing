using MonoTorrent;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace ClipFx.Torrents;

/// <summary>
/// The engine's normal disk writer, plus one thing: when a download finishes we close its files, so
/// the write handles go away and other programs can open them while they seed (reads reopen read-only).
/// </summary>
sealed class SharedFileWriter(IPieceWriter inner) : IPieceWriter
{
    public int MaximumOpenFiles => inner.MaximumOpenFiles;
    public int OpenFiles => inner.OpenFiles;

    public ReusableTask CloseAsync(ITorrentManagerFile file) => inner.CloseAsync(file);
    public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file) => inner.ExistsAsync(file);
    public ReusableTask FlushAsync(ITorrentManagerFile file) => inner.FlushAsync(file);
    public ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite) => inner.MoveAsync(file, fullPath, overwrite);
    public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer) => inner.ReadAsync(file, offset, buffer);
    public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer) => inner.WriteAsync(file, offset, buffer);
    public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles) => inner.SetMaximumOpenFilesAsync(maximumOpenFiles);
    public void Dispose() => inner.Dispose();

    public async Task ReleaseAsync(IEnumerable<ITorrentManagerFile> files)
    {
        foreach (var f in files)
        {
            try { await inner.FlushAsync(f); await inner.CloseAsync(f); } catch { }
        }
    }
}
