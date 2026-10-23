using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipFx.Share.Web;
using Microsoft.Win32;

namespace ClipFx.Share.App;

static class Program
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipFx Share");
    const string InstanceEvent = "Local\\ClipFxShare.Activate";
    static string PendingFile => Path.Combine(DataDir, "pending-send.txt");

    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        var paths = args.Where(a => !a.StartsWith("--")).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        bool background = args.Contains("--background");

        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceEvent, out bool first);
        if (!first)
        {
            // Already running: hand over files from "Send to" and bring it forward.
            if (paths.Length > 0) File.AppendAllLines(PendingFile, paths);
            activate.Set();
            return 0;
        }
        if (paths.Length > 0) File.WriteAllLines(PendingFile, paths);

        ApplicationConfiguration.Initialize();
        var host = new WinHost();
        var node = new ShareNode(DataDir, "windows", host);
        if (node.Settings.StartWithWindows) Startup.Set(true);
        node.StartAsync().GetAwaiter().GetResult();

        var ui = UiServer.Build(node, host, PickUiPort());
        ui.StartAsync().GetAwaiter().GetResult();
        var form = new MainForm(node, host, UiServer.Address(ui), PendingFile);
        host.Attach(form, node);

        new Thread(() => { while (activate.WaitOne()) form.BeginInvoke(form.ShowFromOtherInstance); }) { IsBackground = true }.Start();
        if (background && paths.Length == 0) form.StartHidden();
        Application.Run(form);

        ui.StopAsync().GetAwaiter().GetResult();
        node.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    static int PickUiPort()
    {
        foreach (var p in new[] { 47650, 0 })
        {
            try { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p); l.Start(); int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }
            catch { }
        }
        return 0;
    }
}

static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "ClipFx Share";

    public static bool Get()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue(Name, false);
    }
}
