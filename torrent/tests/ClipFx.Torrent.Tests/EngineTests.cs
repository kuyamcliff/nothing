using System.Diagnostics;
using MonoTorrent;
using Xunit;
using Xunit.Abstractions;

namespace ClipFx.Torrents.Tests;

public class SourceTests
{
    [Fact]
    public async Task ParsesEveryKindOfInput()
    {
        const string hex = "dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c";
        var a = await Sources.ResolveAsync("magnet:?xt=urn:btih:" + hex + "&dn=Big+Buck+Bunny&tr=udp%3A%2F%2Fexplodie.org%3A6969");
        Assert.Equal(hex, a.Id);
        Assert.Equal("Big Buck Bunny", a.Name);
        Assert.Single(a.Magnet!.AnnounceUrls);

        var b = await Sources.ResolveAsync("  " + hex.ToUpperInvariant() + "\n");
        Assert.Equal(hex, b.Id);

        var c = await Sources.ResolveAsync("3WBFL3G4PSSV7MF37AJSHWDQMLNR63I4"); // same hash, base32
        Assert.Equal(hex, c.Id);

        await Assert.ThrowsAsync<UserError>(() => Sources.ResolveAsync("hello"));
        await Assert.ThrowsAsync<UserError>(() => Sources.ResolveAsync("magnet:?xt=urn:btih:zz"));
    }

    [Fact]
    public void FindsMagnetsInsidePastedText()
    {
        var text = "Here: magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=a and also\n<a href=\"magnet:?xt=urn:btih:c9e15763f722f23e98a29decdfae341b98d53056&dn=b\">x</a>";
        var links = Sources.FindLinks(text);
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.StartsWith("magnet:?", l));
    }

    [Fact]
    public async Task TorrentFileAndMagnetTextFile()
    {
        var dir = Directory.CreateTempSubdirectory("cft-src").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[300_000]);
            File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[10]);
            var dict = await new TorrentCreator { PieceLength = 32768 }.CreateAsync(new TorrentFileSource(dir));
            var tp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".torrent");
            File.WriteAllBytes(tp, dict.Encode());
            var s = await Sources.ResolveAsync(tp);
            Assert.NotNull(s.Torrent);
            Assert.Equal(2, s.Torrent!.Files.Count);
            Assert.Equal(s.Id, await Task.FromResult((await Sources.ResolveAsync(new Uri(tp).AbsoluteUri)).Id));

            var bad = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".torrent");
            File.WriteAllText(bad, "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c");
            Assert.NotNull((await Sources.ResolveAsync(bad)).Magnet);
            File.WriteAllText(bad, "garbage");
            await Assert.ThrowsAsync<UserError>(() => Sources.ResolveAsync(bad));
            File.Delete(tp); File.Delete(bad);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void KindFollowsTheBiggestFile()
    {
        var t = Torrent.Load(new TorrentCreator { PieceLength = 16384 }.Create(new TorrentFileSource(MakeDir(("movie.mkv", 100_000), ("subs.srt", 100)))));
        Assert.Equal("video", TorrentClient.KindOf(t));
        t = Torrent.Load(new TorrentCreator { PieceLength = 16384 }.Create(new TorrentFileSource(MakeDir(("a.dat", 100), ("b.dat", 100)))));
        Assert.Equal("folder", TorrentClient.KindOf(t));
    }

    static string MakeDir(params (string Name, int Size)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("cft-kind").FullName;
        foreach (var (n, s) in files) File.WriteAllBytes(Path.Combine(dir, n), new byte[s]);
        return dir;
    }
}

[Collection("swarm")]
public class DownloadTests(ITestOutputHelper log)
{
    static readonly TimeSpan Long = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task DownloadsTorrentFile_FilesMatchExactly_AndIsFast()
    {
        await using var swarm = new Swarm();
        var (dir, tp, t) = await swarm.MakeContentAsync("pack", 600L << 20, 30L << 20, 123_457);
        for (int i = 0; i < 3; i++) await swarm.SeedAsync(t, dir);
        var c = await swarm.ClientAsync();

        var sw = Stopwatch.StartNew();
        var r = await c.AddAsync(new AddRequest(tp, null, null, null, null, null));
        Assert.False(r.Existing);
        var v = await Swarm.WaitAsync(c, r.Id, x => x.Status is Status.Seeding or Status.Finished, Long, log);
        double secs = sw.Elapsed.TotalSeconds;
        v = await Swarm.WaitAsync(c, r.Id, x => x.CompletedAt != null, TimeSpan.FromSeconds(5));
        double mbps = t.Size / secs / 1e6;
        log.WriteLine($"Downloaded {t.Size / 1e6:F0} MB in {secs:F1}s = {mbps:F0} MB/s");

        foreach (var f in t.Files)
            Assert.Equal(Swarm.Hash(Path.Combine(dir, "pack", f.Path)), Swarm.Hash(Path.Combine(c.Settings.SaveDir, "pack", f.Path)));
        Assert.Equal(1.0, v.Progress);
        Assert.NotNull(v.CompletedAt);
        Assert.True(mbps > 40, $"too slow: {mbps:F0} MB/s");

        var again = await c.AddAsync(new AddRequest(tp, null, null, null, null, null));
        Assert.True(again.Existing);
        Assert.Single(c.Torrents());
    }

    [Fact]
    public async Task Magnet_FetchesFileList_ThenDownloadsOnlyChosenFiles()
    {
        await using var swarm = new Swarm();
        var (dir, _, t) = await swarm.MakeContentAsync("mag", 40L << 20, 25L << 20, 5L << 20);
        await swarm.SeedAsync(t, dir);
        var c = await swarm.ClientAsync();

        var magnet = new MagnetLink(t.InfoHashes, t.Name, new List<string> { swarm.TrackerUrl }, null, t.Size).ToV1String();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var p = await c.PreviewAsync(magnet, true, cts.Token);
        Assert.True(p.HasMetadata);
        Assert.Equal(3, p.Files.Count);
        Assert.Equal(t.Size, p.Size);
        var skip = p.Files.First(f => f.Path.Contains("extras")).Index;

        var r = await c.AddAsync(new AddRequest(null, p.Id, null, null, null, [skip]));
        var v = await Swarm.WaitAsync(c, r.Id, x => x.Status is Status.Seeding or Status.Finished, Long, log);
        Assert.Equal(t.Size - (25L << 20), v.Wanted);

        var d = await c.DetailAsync(r.Id);
        Assert.Equal("skip", d!.Files[skip].Priority);
        Assert.True(d.Files[skip].Progress < 1);
        foreach (var f in d.Files.Where(f => f.Index != skip)) Assert.Equal(1.0, f.Progress);
        Assert.True(File.Exists(Path.Combine(swarm.Root, "client", "torrents", r.Id + ".torrent")));

        // Picking the skipped file later downloads it too.
        await c.SetPriorityAsync(r.Id, [skip], "high");
        v = await Swarm.WaitAsync(c, r.Id, x => x.Wanted == t.Size && x.Status is Status.Seeding or Status.Finished, Long, log);
        foreach (var f in t.Files)
            Assert.Equal(Swarm.Hash(Path.Combine(dir, "mag", f.Path)), Swarm.Hash(Path.Combine(c.Settings.SaveDir, "mag", f.Path)));
    }

    [Fact]
    public async Task PauseResume_AndSurvivesAppRestart()
    {
        var clock = Stopwatch.StartNew();
        void Mark(string what) => log.WriteLine($"[{clock.Elapsed.TotalSeconds:F1}s] {what}");
        await using var swarm = new Swarm();
        var (dir, tp, t) = await swarm.MakeContentAsync("resume", 200L << 20, 1L << 20);
        await swarm.SeedAsync(t, dir, uploadLimit: 20 << 20);
        var c = await swarm.ClientAsync();
        var r = await c.AddAsync(new AddRequest(tp, null, null, null, null, null));

        await Swarm.WaitAsync(c, r.Id, x => x.Progress > 0.2, Long, log);
        Mark("pausing");
        await c.PauseAsync(r.Id);
        Mark("paused");
        var paused = c.Torrents().Single();
        Assert.Equal(Status.Paused, paused.Status);
        await Task.Delay(2000);
        var still = c.Torrents().Single();
        Assert.Equal(Status.Paused, still.Status);
        Assert.InRange(still.Done - paused.Done, 0, 4L << 20); // only in-flight blocks may land

        await c.ResumeAsync(r.Id);
        Mark("resumed");
        var mid = await Swarm.WaitAsync(c, r.Id, x => x.Progress > 0.5, Long, log);

        // Quit the app mid-download and start it again.
        Mark("closing app");
        await swarm.CloseAsync(c);
        Mark("closed");
        var c2 = await swarm.ClientAsync();
        Mark("restarted");
        var back = await Swarm.WaitAsync(c2, r.Id, x => x.HasMetadata && x.Status != Status.Checking && x.Progress > 0, TimeSpan.FromSeconds(60), log);
        log.WriteLine($"before restart {mid.Progress:P0}, after {back.Progress:P0}");
        Assert.True(back.Progress >= mid.Progress - 0.05, "progress was lost on restart");

        await Swarm.WaitAsync(c2, r.Id, x => x.Status is Status.Seeding or Status.Finished, Long, log);
        Mark("finished");
        Assert.Equal(Swarm.Hash(Path.Combine(dir, "resume", "part0.bin")), Swarm.Hash(Path.Combine(c2.Settings.SaveDir, "resume", "part0.bin")));

        // A paused torrent stays paused after a restart.
        await c2.PauseAsync(r.Id);
        await swarm.CloseAsync(c2);
        var c3 = await swarm.ClientAsync();
        await Task.Delay(1500);
        Assert.Equal(Status.Paused, c3.Torrents().Single().Status);
    }

    [Fact]
    public async Task SeederVanishes_DownloadContinuesWhenPeersReturn()
    {
        await using var swarm = new Swarm();
        var (dir, tp, t) = await swarm.MakeContentAsync("drop", 150L << 20, 1L << 20);
        var seeder = await swarm.SeedAsync(t, dir, uploadLimit: 15 << 20);
        var c = await swarm.ClientAsync();
        var r = await c.AddAsync(new AddRequest(tp, null, null, null, null, null));
        await Swarm.WaitAsync(c, r.Id, x => x.Progress > 0.25, Long, log);

        await swarm.StopSeederAsync(seeder);   // like Wi-Fi dropping out
        var stalled = await Swarm.WaitAsync(c, r.Id, x => x.Peers == 0, TimeSpan.FromSeconds(60), log);
        await Task.Delay(4000);
        Assert.NotEqual(Status.Error, c.Torrents().Single().Status);

        await swarm.SeedAsync(t, dir);          // a new peer appears on the tracker
        var done = await Swarm.WaitAsync(c, r.Id, x => x.Status is Status.Seeding or Status.Finished, Long, log);
        Assert.True(done.Progress == 1);
        Assert.Equal(Swarm.Hash(Path.Combine(dir, "drop", "part0.bin")), Swarm.Hash(Path.Combine(c.Settings.SaveDir, "drop", "part0.bin")));
    }

    [Fact]
    public async Task Queue_SeedPolicy_Remove()
    {
        await using var swarm = new Swarm();
        var a = await swarm.MakeContentAsync("qa", 400L << 20, 1L << 20);
        var b = await swarm.MakeContentAsync("qb", 20L << 20, 1L << 20);
        await swarm.SeedAsync(a.Torrent, a.Dir, uploadLimit: 10 << 20);
        await swarm.SeedAsync(b.Torrent, b.Dir);
        var c = await swarm.ClientAsync(tweak: s => { s.ActiveDownloads = 1; s.Seeding = "stop"; });

        var ra = await c.AddAsync(new AddRequest(a.TorrentPath, null, null, null, null, null));
        var rb = await c.AddAsync(new AddRequest(b.TorrentPath, null, null, null, null, null));
        // Only one download at a time: the second waits in the queue while the first runs.
        var end = DateTime.UtcNow.AddSeconds(30);
        bool sawQueued = false;
        while (DateTime.UtcNow < end && !sawQueued)
        {
            var all = c.Torrents();
            var va = all.Single(x => x.Id == ra.Id); var vb = all.Single(x => x.Id == rb.Id);
            sawQueued = va.Status is Status.Downloading or Status.Stalled && vb.Status == Status.Queued;
            Assert.False(va.Status is Status.Downloading && vb.Status is Status.Downloading, "both downloading at once");
            await Task.Delay(20);
        }
        Assert.True(sawQueued, "second torrent never waited in the queue");

        // Force-start jumps the queue.
        await c.ResumeAsync(rb.Id, force: true);
        await Swarm.WaitAsync(c, rb.Id, x => x.Status is Status.Downloading or Status.Seeding or Status.Finished, TimeSpan.FromSeconds(30), log);

        // "Stop when finished": both end as Finished, not Seeding.
        await Swarm.WaitAsync(c, ra.Id, x => x.Status == Status.Finished, Long, log);
        await c.PauseAsync(rb.Id);
        await c.ResumeAsync(rb.Id);
        await Swarm.WaitAsync(c, rb.Id, x => x.Status is Status.Finished or Status.Seeding, Long, log);

        var file = Path.Combine(c.Settings.SaveDir, "qa", "part0.bin");
        Assert.True(File.Exists(file));
        Assert.True(await c.RemoveAsync(ra.Id, deleteFiles: true));
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.Combine(c.Settings.SaveDir, "qa")));
        Assert.True(await c.RemoveAsync(rb.Id, deleteFiles: false));
        Assert.True(File.Exists(Path.Combine(c.Settings.SaveDir, "qb", "part0.bin")));
        Assert.Empty(c.Torrents());
    }

    [Fact]
    public async Task RemoveThenAddSameTorrentByMagnet_DownloadsAgain()
    {
        await using var swarm = new Swarm();
        var (dir, tp, t) = await swarm.MakeContentAsync("again", 300L << 20, 1L << 20);
        for (int i = 0; i < 4; i++) await swarm.SeedAsync(t, dir);
        var c = await swarm.ClientAsync(tweak: s => { s.Dht = true; s.ExtraTrackers = true; s.PeerExchange = true; s.LocalPeers = true; s.Seeding = "ratio"; });
        var r = await c.AddAsync(new AddRequest(tp, null, null, null, null, null));
        await Swarm.WaitAsync(c, r.Id, x => x.Status is Status.Seeding or Status.Finished, Long, log);
        Assert.True(await c.RemoveAsync(r.Id, deleteFiles: true));
        var magnet = new MagnetLink(t.InfoHashes, t.Name, new List<string> { swarm.TrackerUrl }, null, t.Size).ToV1String();
        var r2 = await c.AddAsync(new AddRequest(magnet, null, null, null, null, null));
        Assert.Equal(r.Id, r2.Id);
        await Swarm.WaitAsync(c, r.Id, x => x.Status is Status.Seeding or Status.Finished, TimeSpan.FromSeconds(60), log);
        await Swarm.WaitAsync(c, r.Id, x => x.CompletedAt != null, TimeSpan.FromSeconds(5));
        // Other programs (players, antivirus, hashing tools) can open a finished file while it seeds,
        // even ones that refuse to share write access.
        var path = Path.Combine(c.Settings.SaveDir, "again", "part0.bin");
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) Assert.True(fs.Length > 0);
        Assert.Equal(Swarm.Hash(Path.Combine(dir, "again", "part0.bin")), Swarm.Hash(path));
    }

    [Fact]
    public async Task Sequential_DownloadsInOrder()
    {
        await using var swarm = new Swarm();
        var (dir, tp, t) = await swarm.MakeContentAsync("seq", 80L << 20, 1L << 20);
        await swarm.SeedAsync(t, dir, uploadLimit: 8 << 20);
        var c = await swarm.ClientAsync();
        var r = await c.AddAsync(new AddRequest(tp, null, null, null, true, null));
        await Swarm.WaitAsync(c, r.Id, x => x.Progress > 0.3, Long, log);
        var d = await c.DetailAsync(r.Id);
        int firstHalf = d!.PieceMap.Take(d.PieceMap.Length / 2).Sum(), secondHalf = d.PieceMap.Skip(d.PieceMap.Length / 2).Sum();
        log.WriteLine($"first half {firstHalf}, second half {secondHalf}");
        Assert.True(firstHalf > secondHalf * 3, "pieces weren't fetched in order");
        Assert.True(c.Torrents().Single().Sequential);
    }
}
