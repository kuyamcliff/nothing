using ClipFx.Torrents.Web;
using Microsoft.Win32;

namespace ClipFx.Torrents.App;

static class Program
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipFx Torrent");
    const string InstanceEvent = "Local\\ClipFxTorrent.Activate";
    public static string PendingFile => Path.Combine(DataDir, "pending-add.txt");

    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        var inputs = args.Where(a => !a.StartsWith("--")).Where(IsInput).ToArray();
        bool background = args.Contains("--background");

        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceEvent, out bool first);
        if (!first)
        {
            // Already running: hand over magnet links / .torrent files and bring the window forward.
            if (inputs.Length > 0) AppendPending(inputs);
            activate.Set();
            return 0;
        }
        if (inputs.Length > 0) File.WriteAllLines(PendingFile, inputs);

        ApplicationConfiguration.Initialize();
        var host = new WinHost();
        var client = new TorrentClient(DataDir, host);
        host.Attach(client);
        if (client.Settings.StartWithWindows) Startup.Set(true);
        client.StartAsync().GetAwaiter().GetResult();

        var ui = UiServer.Build(client, host, PickUiPort());
        ui.StartAsync().GetAwaiter().GetResult();
        var form = new MainForm(client, host, UiServer.Address(ui));
        host.Attach(form);

        new Thread(() => { while (activate.WaitOne()) form.BeginInvoke(form.ShowFromOtherInstance); }) { IsBackground = true }.Start();
        if (background && inputs.Length == 0) form.StartHidden();
        Application.Run(form);

        ui.StopAsync().GetAwaiter().GetResult();
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    static bool IsInput(string a) =>
        a.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || a.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        (a.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

    static void AppendPending(string[] lines)
    {
        for (int i = 0; i < 20; i++)
        {
            try { File.AppendAllLines(PendingFile, lines); return; }
            catch (IOException) { Thread.Sleep(50); }
        }
    }

    static int PickUiPort()
    {
        foreach (var p in new[] { 47670, 0 })
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
    const string Name = "ClipFx Torrent";

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

/// <summary>Makes magnet links and .torrent files open in this app.</summary>
static class Associations
{
    const string MagnetProg = "ClipFxTorrent.magnet", FileProg = "ClipFxTorrent.torrent";

    static string Command => $"\"{Environment.ProcessPath}\" \"%1\"";

    public static bool IsDefault()
    {
        try
        {
            using var uc = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\magnet\UserChoice");
            if (uc?.GetValue("ProgId") is string prog) return prog == MagnetProg;
            using var cmd = Registry.ClassesRoot.OpenSubKey(@"magnet\shell\open\command");
            return cmd?.GetValue(null) is string c && c.Contains(Path.GetFileName(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Register()
    {
        var exe = Environment.ProcessPath!;
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{MagnetProg}"))
        {
            k.SetValue(null, "Magnet link");
            k.SetValue("URL Protocol", "");
            using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue(null, $"\"{exe}\",0");
            using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue(null, Command);
        }
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{FileProg}"))
        {
            k.SetValue(null, "Torrent file");
            using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue(null, $"\"{exe}\",0");
            using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue(null, Command);
        }
        // The protocol itself (used when no app was picked in Settings yet).
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet"))
        {
            k.SetValue(null, "URL:Magnet link");
            k.SetValue("URL Protocol", "");
            using var c = k.CreateSubKey(@"shell\open\command");
            c.SetValue(null, Command);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent"))
        {
            k.SetValue(null, FileProg);
            k.SetValue("Content Type", "application/x-bittorrent");
            using var o = k.CreateSubKey("OpenWithProgids");
            o.SetValue(FileProg, Array.Empty<byte>(), RegistryValueKind.None);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\ClipFx\ClipFx Torrent\Capabilities"))
        {
            k.SetValue("ApplicationName", "ClipFx Torrent");
            k.SetValue("ApplicationDescription", "Torrent downloader");
            using (var u = k.CreateSubKey("URLAssociations")) u.SetValue("magnet", MagnetProg);
            using (var f = k.CreateSubKey("FileAssociations")) f.SetValue(".torrent", FileProg);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            k.SetValue("ClipFx Torrent", @"Software\ClipFx\ClipFx Torrent\Capabilities");
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
