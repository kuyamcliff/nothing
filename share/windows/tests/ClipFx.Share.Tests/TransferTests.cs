using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using ClipFx.Share;
using Xunit;
using Xunit.Abstractions;

namespace ClipFx.Share.Tests;

public class TransferTests(ITestOutputHelper log)
{
    [Fact]
    public async Task SendsFilesFoldersEmptiesAndText()
    {
        await using var a = await Node.StartAsync("A");
        await using var b = await Node.StartAsync("B");
        var src = T.Temp("src");
        var folder = Path.Combine(src, "Photos");
        T.RandomFile(folder, "2024/beach.jpg", 3_500_000, 1);
        T.RandomFile(folder, "2024/sub/tiny.txt", 10, 2);
        T.RandomFile(folder, "empty.bin", 0, 3);
        Directory.CreateDirectory(Path.Combine(folder, "Empty Folder"));
        var big = T.RandomFile(src, "video.mp4", 40_000_000, 4);
        File.SetLastWriteTimeUtc(big, new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc));

        var peer = await a.Pair(b);
        var id = a.N.Send([peer], ShareNode.Collect([folder, big], "hello from A")).Single();
        var t = await T.Done(a.N, id);
        Assert.Equal(TransferState.Completed, t.State);

        var recv = await T.Incoming(b.N, x => x.State == TransferState.Completed);
        Assert.Equal("hello from A", recv.Text);
        T.SameTree(folder, Path.Combine(b.Save, "Photos"));
        Assert.Equal(T.Hash(big), T.Hash(Path.Combine(b.Save, "video.mp4")));
        Assert.Equal(new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(b.Save, "video.mp4")));
        Assert.Empty(Directory.GetFiles(b.Save, "*" + IncomingSession.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AsksAndRespectsTheAnswer()
    {
        await using var a = await Node.StartAsync("A");
        await using var b = await Node.StartAsync("B", ask: true);
        var src = T.Temp("src");
        var f = T.RandomFile(src, "doc.pdf", 1000);
        var peer = await a.Pair(b);

        var id1 = a.N.Send([peer], ShareNode.Collect([f])).Single();
        var req = await T.Incoming(b.N, x => x.State == TransferState.Waiting);
        Assert.Equal("A", req.Peer.Name);
        Assert.Equal("doc.pdf", req.Title);
        await T.WaitFor(a.N, id1, x => x.State == TransferState.Waiting);
        Assert.True(b.N.Decline(req.Id));
        Assert.Equal(TransferState.Declined, (await T.Done(a.N, id1)).State);
        Assert.False(File.Exists(Path.Combine(b.Save, "doc.pdf")));

        var id2 = a.N.Send([peer], ShareNode.Collect([f])).Single();
        var req2 = await T.Incoming(b.N, x => x.State == TransferState.Waiting);
        Assert.True(b.N.Accept(req2.Id, trust: true));
        Assert.Equal(TransferState.Completed, (await T.Done(a.N, id2)).State);
        Assert.Contains(a.N.Settings.DeviceId, b.N.Settings.TrustedDevices);

        // Trusted now: no question the next time.
        var id3 = a.N.Send([peer], ShareNode.Collect([f])).Single();
        Assert.Equal(TransferState.Completed, (await T.Done(a.N, id3)).State);
        Assert.True(File.Exists(Path.Combine(b.Save, "doc (2).pdf")));
    }

    [Fact]
    public async Task PausesAndResumes()
    {
        await using var a = await Node.StartAsync("A", cfg: s => s.ChunkMB = 2);
        await using var b = await Node.StartAsync("B");
        var src = T.Temp("src");
        var f = T.RandomFile(src, "big.iso", 300_000_000, 7);
        var peer = await a.Pair(b);
        var id = a.N.Send([peer], ShareNode.Collect([f])).Single();
        await T.WaitFor(a.N, id, x => x.DoneBytes > 20_000_000);
        a.N.Pause(id);
        var paused = await T.WaitFor(a.N, id, x => x.State == TransferState.Paused);
        await Task.Delay(500);
        var at = a.N.Transfer(id)!.DoneBytes;
        await Task.Delay(500);
        Assert.Equal(at, a.N.Transfer(id)!.DoneBytes); // really stopped
        Assert.True(at < 300_000_000);
        a.N.Resume(id);
        Assert.Equal(TransferState.Completed, (await T.Done(a.N, id)).State);
        Assert.Equal(T.Hash(f), T.Hash(Path.Combine(b.Save, "big.iso")));
    }

    [Fact]
    public async Task ReceiverCanPauseAndCancel()
    {
        await using var a = await Node.StartAsync("A", cfg: s => s.ChunkMB = 1);
        await using var b = await Node.StartAsync("B");
        var src = T.Temp("src");
        var f = T.RandomFile(src, "big.bin", 400_000_000, 8);
        var peer = await a.Pair(b);
        var id = a.N.Send([peer], ShareNode.Collect([f])).Single();
        var r = await T.Incoming(b.N, x => x.State == TransferState.Active && x.DoneBytes > 10_000_000);
        b.N.Pause(r.Id);
        await T.WaitFor(a.N, id, x => x.State == TransferState.PausedByPeer);
        b.N.Resume(r.Id);
        await T.WaitFor(a.N, id, x => x.State == TransferState.Active);
        b.N.Cancel(r.Id);
        var t = await T.Done(a.N, id);
        Assert.Equal(TransferState.Cancelled, t.State);
        await Task.Delay(300);
        Assert.Empty(Directory.GetFiles(b.Save, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SurvivesReceiverRestartAndResumesWhereItStopped()
    {
        await using var a = await Node.StartAsync("A", cfg: s => s.ChunkMB = 4);
        var b = await Node.StartAsync("B");
        try
        {
            var src = T.Temp("src");
            var f = T.RandomFile(src, "movie.mkv", 400_000_000, 9);
            var peer = await a.Pair(b);
            var id = a.N.Send([peer], ShareNode.Collect([f])).Single();
            await T.WaitFor(a.N, id, x => x.DoneBytes > 100_000_000);
            await b.RestartAsync(); // like the receiver PC rebooting or Wi-Fi dropping
            await T.WaitFor(a.N, id, x => x.State == TransferState.Reconnecting || x.State == TransferState.Active);
            var t = await T.Done(a.N, id);
            Assert.Equal(TransferState.Completed, t.State);
            Assert.Equal(T.Hash(f), T.Hash(Path.Combine(b.Save, "movie.mkv")));
        }
        finally { await b.DisposeAsync(); }
    }

    [Fact]
    public async Task SenderRestartCanBeResumed()
    {
        var a = await Node.StartAsync("A", cfg: s => s.ChunkMB = 4);
        await using var b = await Node.StartAsync("B");
        try
        {
            var src = T.Temp("src");
            var f = T.RandomFile(src, "backup.zip", 300_000_000, 10);
            var peer = await a.Pair(b);
            var id = a.N.Send([peer], ShareNode.Collect([f])).Single();
            await T.WaitFor(a.N, id, x => x.DoneBytes > 50_000_000);
            await a.RestartAsync();
            await a.Pair(b);
            var t = a.N.Transfer(id)!;
            Assert.True(t.CanResume);
            a.N.Resume(id);
            Assert.Equal(TransferState.Completed, (await T.Done(a.N, id)).State);
            Assert.Equal(T.Hash(f), T.Hash(Path.Combine(b.Save, "backup.zip")));
        }
        finally { await a.DisposeAsync(); }
    }

    [Fact]
    public async Task SortsReceivedFilesByType()
    {
        await using var a = await Node.StartAsync("A");
        await using var b = await Node.StartAsync("B", cfg: s => s.OrganizeByType = true);
        var src = T.Temp("src");
        var files = new[] { "clip.MP4", "photo.jpg", "song.mp3", "report.pdf", "game.apk", "setup.exe", "backup.zip", "data.7z", "notes.xyz", "noext" }
            .Select((n, k) => T.RandomFile(src, n, 1000 + k, k)).ToList();
        var folder = Path.Combine(src, "Trip");
        T.RandomFile(folder, "day1/a.jpg", 500, 50);
        T.RandomFile(folder, "video.mp4", 500, 51);
        var id = a.N.Send([await a.Pair(b)], ShareNode.Collect([.. files, folder])).Single();
        Assert.Equal(TransferState.Completed, (await T.Done(a.N, id)).State);
        string P(params string[] p) => Path.Combine([b.Save, .. p]);
        Assert.True(File.Exists(P("Videos", "clip.MP4")));
        Assert.True(File.Exists(P("Images", "photo.jpg")));
        Assert.True(File.Exists(P("Music", "song.mp3")));
        Assert.True(File.Exists(P("Documents", "report.pdf")));
        Assert.True(File.Exists(P("Apps", "game.apk")));
        Assert.True(File.Exists(P("Apps", "setup.exe")));
        Assert.True(File.Exists(P("Archives", "backup.zip")));
        Assert.True(File.Exists(P("Archives", "data.7z")));
        Assert.True(File.Exists(P("Other", "notes.xyz")));
        Assert.True(File.Exists(P("Other", "noext")));
        // A sent folder keeps its structure.
        T.SameTree(folder, P("Folders", "Trip"));
        var recv = await T.Incoming(b.N, x => x.State == TransferState.Completed);
        Assert.Equal(b.Save, recv.OpenPath);
    }

    [Fact]
    public async Task ConflictPolicies()
    {
        await using var a = await Node.StartAsync("A");
        await using var b = await Node.StartAsync("B");
        var src = T.Temp("src");
        var f = T.RandomFile(src, "notes.txt", 5000, 11);
        Directory.CreateDirectory(b.Save);
        File.WriteAllText(Path.Combine(b.Save, "notes.txt"), "old");
        var peer = await a.Pair(b);

        b.N.UpdateSettings(s => s.Conflict = "skip");
        await T.Done(a.N, a.N.Send([peer], ShareNode.Collect([f])).Single());
        Assert.Equal("old", File.ReadAllText(Path.Combine(b.Save, "notes.txt")));

        b.N.UpdateSettings(s => s.Conflict = "rename");
        await T.Done(a.N, a.N.Send([peer], ShareNode.Collect([f])).Single());
        Assert.Equal(T.Hash(f), T.Hash(Path.Combine(b.Save, "notes (2).txt")));

        b.N.UpdateSettings(s => s.Conflict = "overwrite");
        await T.Done(a.N, a.N.Send([peer], ShareNode.Collect([f])).Single());
        Assert.Equal(T.Hash(f), T.Hash(Path.Combine(b.Save, "notes.txt")));
    }

    [Fact]
    public async Task SendsToSeveralDevicesAtOnce()
    {
        await using var a = await Node.StartAsync("A");
        await using var b = await Node.StartAsync("B");
        await using var c = await Node.StartAsync("C");
        await using var d = await Node.StartAsync("D");
        var src = T.Temp("src");
        var f = T.RandomFile(src, "deck.pptx", 60_000_000, 12);
        var ids = a.N.Send([await a.Pair(b), await a.Pair(c), await a.Pair(d)], ShareNode.Collect([f]));
        Assert.Equal(3, ids.Count);
        foreach (var id in ids) Assert.Equal(TransferState.Completed, (await T.Done(a.N, id)).State);
        foreach (var n in new[] { b, c, d }) Assert.Equal(T.Hash(f), T.Hash(Path.Combine(n.Save, "deck.pptx")));
    }

    [Fact]
    public async Task RejectsDamagedData()
    {
        await using var b = await Node.StartAsync("B");
        await using var c = await Conn.ConnectAsync(IPAddress.Loopback, b.Port, TimeSpan.FromSeconds(3), default);
        await c.WriteMessageAsync(new JsonObject
        {
            ["op"] = "offer", ["session"] = "s1", ["from"] = Json.From(new DeviceInfo("x", "X", "test", 1)),
            ["items"] = Json.From(new[] { new ItemInfo(0, "../../escape.bin", 4, 0, ItemType.File) }),
        }, default);
        var offer = await c.ReadMessageAsync(default);
        Assert.True(offer!.Bool("accepted"));
        var key = offer.Str("key");
        await c.WriteMessageAsync(new JsonObject { ["op"] = "data", ["session"] = "s1", ["key"] = key, ["parts"] = new JsonArray(new JsonObject { ["i"] = 0, ["off"] = 0, ["len"] = 4 }) }, default);
        await c.WriteAsync(new byte[] { 1, 2, 3, 4 }, default);
        await c.WriteUInt32Async(12345, default); // wrong checksum
        Assert.Equal("crc", (await c.ReadMessageAsync(default))!.Str("error"));

        await c.WriteMessageAsync(new JsonObject { ["op"] = "data", ["session"] = "s1", ["key"] = "wrong", ["parts"] = new JsonArray(new JsonObject { ["i"] = 0, ["off"] = 0, ["len"] = 4 }) }, default);
        await c.WriteAsync(new byte[] { 1, 2, 3, 4 }, default);
        await c.WriteUInt32Async(0, default);
        Assert.Equal("forbidden", (await c.ReadMessageAsync(default))!.Str("error"));

        await c.WriteMessageAsync(new JsonObject { ["op"] = "data", ["session"] = "s1", ["key"] = key, ["parts"] = new JsonArray(new JsonObject { ["i"] = 0, ["off"] = 0, ["len"] = 4 }) }, default);
        await c.WriteAsync(new byte[] { 1, 2, 3, 4 }, default);
        await c.WriteUInt32Async(System.IO.Hashing.Crc32.HashToUInt32(new byte[] { 1, 2, 3, 4 }), default);
        Assert.True((await c.ReadMessageAsync(default))!.Bool("ok"));
        await c.WriteMessageAsync(new JsonObject { ["op"] = "done", ["session"] = "s1", ["key"] = key }, default);
        Assert.True((await c.ReadMessageAsync(default))!.Bool("ok"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(b.Save, "escape.bin")));
        Assert.False(File.Exists(Path.Combine(b.Data, "..", "escape.bin")));
    }

    [Fact]
    public async Task ThousandsOfSmallFiles()
    {
        await using var a = await Node.StartAsync("A");
        await using var b = await Node.StartAsync("B");
        var src = T.Temp("src");
        var root = Path.Combine(src, "project");
        var rng = new Random(5);
        for (int i = 0; i < 3000; i++)
        {
            var p = Path.Combine(root, $"d{i % 37}", $"f{i}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var bytes = new byte[rng.Next(0, 8192)];
            rng.NextBytes(bytes);
            File.WriteAllBytes(p, bytes);
        }
        var peer = await a.Pair(b);
        var sw = Stopwatch.StartNew();
        var id = a.N.Send([peer], ShareNode.Collect([root])).Single();
        Assert.Equal(TransferState.Completed, (await T.Done(a.N, id)).State);
        log.WriteLine($"3000 small files in {sw.ElapsedMilliseconds} ms ({3000 / sw.Elapsed.TotalSeconds:F0} files/s)");
        T.SameTree(root, Path.Combine(b.Save, "project"));
    }

    [Fact]
    public async Task Speed()
    {
        // The engine's own throughput (network, checksums, protocol), without this machine's disk.
        IncomingSession.NoWrite = true;
        try
        {
            await using var a = await Node.StartAsync("A");
            await using var b = await Node.StartAsync("B");
            var src = T.Temp("src");
            long size = 1_000_000_000;
            var f = Path.Combine(src, "speed.bin");
            using (var fs = File.Create(f)) fs.SetLength(size);
            var peer = await a.Pair(b);
            double best = 0;
            for (int run = 0; run < 3; run++)
            {
                var sw = Stopwatch.StartNew();
                var id = a.N.Send([peer], ShareNode.Collect([f])).Single();
                Assert.Equal(TransferState.Completed, (await T.Done(a.N, id, 300)).State);
                double mbps = size / 1e6 / sw.Elapsed.TotalSeconds;
                log.WriteLine($"run {run}: 1 GB in {sw.Elapsed.TotalSeconds:F2} s = {mbps:F0} MB/s");
                best = Math.Max(best, mbps);
            }
            Assert.True(best > 300, $"Too slow: {best:F0} MB/s");
        }
        finally { IncomingSession.NoWrite = false; }
    }
}
