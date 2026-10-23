// Command line used for interoperability and speed tests.
//   receive --data DIR --save DIR [--port N] [--organize]
//   send --data DIR --to HOST:PORT [--streams N] [--text T] PATH...
using System.Diagnostics;
using ClipFx.Share;

string? Opt(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var data = Opt("--data") ?? throw new ArgumentException("--data required");

switch (args.FirstOrDefault())
{
    case "receive":
    {
        var save = Opt("--save")!;
        var node = new ShareNode(data, "dotnet");
        node.UpdateSettings(s => { s.DeviceName = ".NET receiver"; s.SaveDir = save; s.AskBeforeReceive = false; s.Port = int.Parse(Opt("--port") ?? "0"); s.OrganizeByType = args.Contains("--organize"); });
        await node.StartAsync(discovery: false);
        Console.WriteLine($"READY {node.Port}");
        var reported = new HashSet<string>();
        while (true)
        {
            await Task.Delay(100);
            foreach (var t in node.Transfers())
                if (States.IsFinal(t.State) && reported.Add(t.Id)) Console.WriteLine($"DONE {t.State} {t.TotalBytes}");
        }
    }
    case "send":
    {
        var skip = new HashSet<string> { "--data", "--to", "--streams", "--text" };
        var rest = args.Skip(1).ToList();
        var paths = rest.Where((a, k) => !a.StartsWith("--") && (k == 0 || !skip.Contains(rest[k - 1]))).ToList();
        var node = new ShareNode(data, "dotnet");
        node.UpdateSettings(s => { s.DeviceName = ".NET sender"; s.Port = 0; s.Streams = int.Parse(Opt("--streams") ?? "4"); });
        await node.StartAsync(discovery: false);
        var peer = await node.AddManualAsync(Opt("--to")!);
        if (peer is null) { Console.WriteLine("UNREACHABLE"); return 2; }
        var sw = Stopwatch.StartNew();
        var id = node.Send([peer.Id], ShareNode.Collect(paths, Opt("--text"))).Single();
        var t = node.Transfer(id)!;
        while (!States.IsFinal(t.State)) { await Task.Delay(20); t = node.Transfer(id)!; }
        Console.WriteLine($"RESULT {t.State} {t.TotalBytes / 1e6 / sw.Elapsed.TotalSeconds:F0} MB/s {t.Error}");
        await node.DisposeAsync();
        return t.State == TransferState.Completed ? 0 : 1;
    }
    default:
        Console.WriteLine("usage: receive|send");
        return 64;
}
