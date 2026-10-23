using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ClipFx.Share;
using Xunit;

namespace ClipFx.Share.Tests;

public sealed class Node : IAsyncDisposable
{
    public ShareNode N { get; private set; }
    public string Data { get; }
    public string Save { get; }
    public int Port { get; }

    Node(string data, string save, int port, ShareNode n) { Data = data; Save = save; Port = port; N = n; }

    public static async Task<Node> StartAsync(string name, bool ask = false, int? port = null, string? data = null, Action<ShareSettings>? cfg = null)
    {
        data ??= T.Temp("node-" + name);
        Directory.CreateDirectory(data);
        var save = Path.Combine(data, "Received");
        int p = port ?? FreePort();
        var n = new ShareNode(data, "test");
        n.UpdateSettings(s => { s.DeviceName = name; s.SaveDir = save; s.AskBeforeReceive = ask; s.Port = p; cfg?.Invoke(s); });
        await n.StartAsync(discovery: false);
        return new Node(data, save, n.Port, n);
    }

    public async Task RestartAsync()
    {
        await N.DisposeAsync();
        await Task.Delay(200);
        N = new ShareNode(Data, "test");
        await N.StartAsync(discovery: false);
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async Task<string> Pair(Node other) => (await N.AddManualAsync($"127.0.0.1:{other.Port}"))!.Id;

    public async ValueTask DisposeAsync() => await N.DisposeAsync();
}

public static class T
{
    static readonly string Root = CreateRoot();

    static string CreateRoot()
    {
        // Remove leftovers of earlier runs (their process is gone).
        foreach (var old in Directory.GetDirectories(Path.GetTempPath(), "cfs-tests-*"))
        {
            if (int.TryParse(Path.GetFileName(old)["cfs-tests-".Length..], out int pid) && pid != Environment.ProcessId)
            {
                bool alive;
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); alive = !p.HasExited; } catch { alive = false; }
                if (!alive) try { Directory.Delete(old, true); } catch { }
            }
        }
        var root = Path.Combine(Path.GetTempPath(), $"cfs-tests-{Environment.ProcessId}");
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, true); } catch { } };
        return root;
    }

    /// <summary>A fresh folder that is deleted when the test run ends.</summary>
    public static string Temp(string name) => Directory.CreateDirectory(Path.Combine(Root, $"{name}-{Guid.NewGuid():N}")).FullName;

    public static async Task<TransferView> WaitFor(ShareNode n, string id, Func<TransferView, bool> cond, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var t = n.Transfer(id);
            if (t != null && cond(t)) return t;
            if (DateTime.UtcNow > until) throw new TimeoutException($"State: {t?.State} {t?.Error} {t?.DoneBytes}/{t?.TotalBytes}");
            await Task.Delay(20);
        }
    }

    public static Task<TransferView> Done(ShareNode n, string id, int seconds = 120) =>
        WaitFor(n, id, t => States.IsFinal(t.State), seconds);

    public static async Task<TransferView> Incoming(ShareNode n, Func<TransferView, bool>? cond = null, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var t = n.Transfers().FirstOrDefault(x => x.Direction == Direction.Receive && (cond?.Invoke(x) ?? true));
            if (t != null) return t;
            if (DateTime.UtcNow > until) throw new TimeoutException("No incoming transfer.");
            await Task.Delay(20);
        }
    }

    public static string RandomFile(string dir, string name, long size, int seed = 1)
    {
        var path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rng = new Random(seed);
        var buf = new byte[1024 * 1024];
        using var fs = File.Create(path);
        long left = size;
        while (left > 0)
        {
            rng.NextBytes(buf);
            int n = (int)Math.Min(buf.Length, left);
            fs.Write(buf, 0, n);
            left -= n;
        }
        return path;
    }

    public static string Hash(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static void SameTree(string expected, string actual)
    {
        var a = Directory.EnumerateFileSystemEntries(expected, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(expected, p)).OrderBy(x => x).ToList();
        var b = Directory.EnumerateFileSystemEntries(actual, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(actual, p)).OrderBy(x => x).ToList();
        Assert.Equal(a, b);
        foreach (var rel in a.Where(r => File.Exists(Path.Combine(expected, r))))
            Assert.Equal(Hash(Path.Combine(expected, rel)), Hash(Path.Combine(actual, rel)));
    }
}
