// Runs ClipFx Manager with simulated traffic and firewall, on any OS.
// Usage: dotnet run -- [--port 47613] [--data DIR] [--ui DIR] [--quiet]
using ClipFx.Platform.Simulated;
using ClipFx.Web;

string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

var options = new ServerOptions
{
    Port = int.TryParse(Arg("--port"), out var p) ? p : ServerOptions.DefaultPort,
    DataDir = Arg("--data") ?? Path.Combine(Path.GetTempPath(), "clipfx-dev"),
    UiDir = Arg("--ui"),
};
var app = ServerHost.Build(options, SimulatedPlatform.Create(autoTraffic: !args.Contains("--quiet")));
app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"ClipFx dev host on {ServerHost.BoundAddress(app)} (data: {options.DataDir})"));
app.Run();
