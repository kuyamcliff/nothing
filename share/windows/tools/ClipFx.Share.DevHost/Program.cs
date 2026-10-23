// Runs the real ClipFx Share engine and UI on any OS for development and UI tests.
// Usage: --name NAME --data DIR --ui-port N [--port N] [--ui DIR] [--ask] [--pick PATH;PATH]
using ClipFx.Share;
using ClipFx.Share.Web;

string? Opt(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var data = Opt("--data") ?? Path.Combine(Path.GetTempPath(), "cfs-dev");
var node = new ShareNode(data, "windows");
node.UpdateSettings(s =>
{
    s.DeviceName = Opt("--name") ?? "Dev PC";
    s.Port = int.Parse(Opt("--port") ?? "0");
    s.SaveDir = Opt("--save") ?? Path.Combine(data, "Received");
    if (args.Contains("--ask")) s.AskBeforeReceive = true;
    if (args.Contains("--no-ask")) s.AskBeforeReceive = false;
});
await node.StartAsync(discovery: !args.Contains("--no-discovery"));
var app = UiServer.Build(node, new DevActions(Opt("--pick")), int.Parse(Opt("--ui-port") ?? "47640"), Opt("--ui"));
app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"UI {UiServer.Address(app)} TRANSFER {node.Port}"));
app.Run();

sealed class DevActions(string? pick) : IHostActions
{
    public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>(pick?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? []);
    public Task<string?> PickFolderAsync(string? title = null) => Task.FromResult(pick?.Split(';').FirstOrDefault(Directory.Exists));
    public void Reveal(string path) => Console.WriteLine("REVEAL " + path);
    public void CopyText(string text) => Console.WriteLine("COPY " + text);
    public bool StartWithWindows { get; set; }
}
