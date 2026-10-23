using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace ClipFx.Torrents.App;

sealed class MainForm : Form
{
    readonly TorrentClient _client;
    readonly WinHost _host;
    readonly Uri _home;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _cancelItem;
    readonly System.Windows.Forms.Timer _trayTimer = new() { Interval = 1000 };
    System.Windows.Forms.Timer? _countdown;
    bool _quitting, _startHidden, _ready, _loaded;
    string? _lastNotifyPath;

    public MainForm(TorrentClient client, WinHost host, Uri home)
    {
        _client = client; _host = host; _home = home;
        Text = "ClipFx Torrent";
        using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico")) if (s != null) Icon = new Icon(s);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1320, 840);
        MinimumSize = new Size(940, 600);
        bool dark = SystemDark();
        BackColor = dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(242, 242, 243);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open ClipFx Torrent", null, (_, _) => ShowWindow());
        menu.Items.Add("Pause all", null, async (_, _) => await _client.PauseAllAsync());
        menu.Items.Add("Resume all", null, async (_, _) => await _client.ResumeAllAsync());
        _cancelItem = new ToolStripMenuItem("Cancel", null, (_, _) => CancelCountdown()) { Visible = false };
        menu.Items.Add(_cancelItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray = new NotifyIcon { Icon = Icon, Text = "ClipFx Torrent", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => { if (_lastNotifyPath != null) host.Reveal(_lastNotifyPath); else ShowWindow(); };
        _trayTimer.Tick += (_, _) => UpdateTrayText();
        _trayTimer.Start();

        HandleCreated += (_, _) => SetDarkTitleBar(dark);
        Load += async (_, _) => await InitAsync();
        FormClosing += OnClosing;
    }

    public void StartHidden() { _startHidden = true; WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; _ = InitAsync(); }
        base.SetVisibleCore(value);
    }

    void UpdateTrayText()
    {
        try
        {
            var g = _client.Global();
            var text = g.Down > 0 || g.Up > 0 ? $"ClipFx Torrent\n↓ {Rate(g.Down)}  ↑ {Rate(g.Up)}" : "ClipFx Torrent";
            if (text.Length > 63) text = text[..63];
            if (_tray.Text != text) _tray.Text = text;
        }
        catch { }
    }

    static string Rate(long b)
    {
        string[] u = ["B/s", "KB/s", "MB/s", "GB/s"];
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    async Task InitAsync()
    {
        if (_ready) return;
        _ready = true;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (MessageBox.Show("Microsoft Edge WebView2 Runtime is required. Download it now?", "ClipFx Torrent", MessageBoxButtons.YesNo) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
            Quit();
            return;
        }
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) =>
        {
            var u = new Uri(e.Uri);
            if (u.Scheme == "magnet") { e.Cancel = true; AddLinks([e.Uri]); return; }
            if (!(u.Host == _home.Host && u.Port == _home.Port)) e.Cancel = true;
        };
        core.WebMessageReceived += OnMessage;
        core.NavigationCompleted += async (_, _) => { _loaded = true; await Task.Delay(300); DeliverPending(); };
        core.Navigate(_home.ToString());
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            switch (doc.RootElement.GetProperty("type").GetString())
            {
                case "theme":
                    bool dark = doc.RootElement.GetProperty("dark").GetBoolean();
                    SetDarkTitleBar(dark);
                    BackColor = dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(242, 242, 243);
                    break;
                case "drop":
                    var paths = e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).ToArray();
                    Script("addPaths", paths);
                    break;
            }
        }
        catch { }
    }

    void Script(string fn, string[] items)
    {
        if (items.Length == 0 || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.ExecuteScriptAsync($"window.cft && window.cft.{fn}({JsonSerializer.Serialize(items)})");
    }

    void AddLinks(string[] links) => Script("addLinks", links);

    void DeliverPending()
    {
        if (!_loaded) return;
        try
        {
            if (!File.Exists(Program.PendingFile)) return;
            var lines = File.ReadAllLines(Program.PendingFile).Where(p => p.Length > 0).Distinct().ToArray();
            File.Delete(Program.PendingFile);
            if (lines.Length == 0) return;
            ShowWindow();
            AddLinks(lines);
        }
        catch { }
    }

    public void ShowFromOtherInstance() { ShowWindow(); DeliverPending(); }

    void ShowWindow()
    {
        _startHidden = false;
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void Notify(string text, string? path)
    {
        _lastNotifyPath = path;
        _tray.ShowBalloonTip(5000, "ClipFx Torrent", text, ToolTipIcon.None);
    }

    /// <summary>"When done": gives the user 30 seconds to cancel before sleeping or shutting down.</summary>
    public void Countdown(string action)
    {
        CancelCountdown();
        string verb = action switch { "sleep" => "Sleeping", "hibernate" => "Hibernating", _ => "Shutting down" };
        int left = 30;
        _cancelItem.Text = $"Cancel {verb.ToLowerInvariant()}";
        _cancelItem.Visible = true;
        _lastNotifyPath = null;
        _tray.ShowBalloonTip(10000, "ClipFx Torrent", $"All downloads finished. {verb} in {left} seconds. Right-click the tray icon to cancel.", ToolTipIcon.None);
        _countdown = new System.Windows.Forms.Timer { Interval = 1000 };
        _countdown.Tick += (_, _) =>
        {
            if (--left > 0) return;
            CancelCountdown();
            WinHost.RunPowerAction(action);
        };
        _countdown.Start();
    }

    void CancelCountdown()
    {
        _countdown?.Stop();
        _countdown?.Dispose();
        _countdown = null;
        _cancelItem.Visible = false;
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_quitting && e.CloseReason == CloseReason.UserClosing && _client.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _tray.Visible = false;
    }

    void Quit() { _quitting = true; _tray.Visible = false; Application.Exit(); }

    static bool SystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void SetDarkTitleBar(bool dark)
    {
        if (!IsHandleCreated) return;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int));
    }
}
