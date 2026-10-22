using ClipFx.Platform.Windows;
using ClipFx.Web;
using Microsoft.Extensions.Hosting;

namespace ClipFx.App;

static class Program
{
    public const string ServiceName = "ClipFxManager";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--service")) return RunService();
        if (args.Contains("--install")) return ServiceSetup.Install();
        if (args.Contains("--start-service")) return ServiceSetup.EnsureRunning();
        if (args.Contains("--stop-service")) return ServiceSetup.Stop();
        if (args.Contains("--uninstall")) return ServiceSetup.Uninstall();
        return Gui.Run();
    }

    static int RunService()
    {
        var app = ServerHost.Build(new ServerOptions(), WindowsPlatform.Create(),
            b => b.Host.UseWindowsService(o => o.ServiceName = ServiceName));
        app.Run();
        return 0;
    }
}
