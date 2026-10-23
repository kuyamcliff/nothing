using Xunit;
using Xunit.Abstractions;

namespace ClipFx.Torrents.Tests;

/// <summary>Needs real internet; runs when CFT_INTERNET=1.</summary>
public class InternetTests(ITestOutputHelper log)
{
    [Fact]
    public async Task DhtJoinsTheNetwork()
    {
        if (Environment.GetEnvironmentVariable("CFT_INTERNET") != "1") return;
        var dir = Directory.CreateTempSubdirectory("cft-dht").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new Settings
            {
                SaveDir = Path.Combine(dir, "dl"), Port = Swarm.FreePort(), PortForwarding = false, LocalPeers = false, ExtraTrackers = false, KeepAwake = false,
            }));
            await using var c = new TorrentClient(dir);
            await c.StartAsync();
            // A bare magnet (no trackers) makes the engine start its DHT.
            await c.AddAsync(new AddRequest("magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c", null, null, null, null, null));
            var end = DateTime.UtcNow.AddSeconds(90);
            GlobalView g;
            do { await Task.Delay(2000); g = c.Global(); log.WriteLine($"dht {g.DhtState} nodes {g.DhtNodes}"); }
            while (g.DhtNodes < 50 && DateTime.UtcNow < end);
            Assert.True(g.DhtNodes >= 50, $"DHT only has {g.DhtNodes} nodes ({g.DhtState})");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
