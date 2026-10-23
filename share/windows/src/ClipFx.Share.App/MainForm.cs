using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace ClipFx.Share.App;

sealed class MainForm : Form
{
    readonly ShareNode _node;
    readonly Uri _home;
    readonly string _pendingFile;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon _tray;
    bool _quitting, _startHidden, _ready;
    string? _lastNotifyPath;

    public MainForm(ShareNode node, WinHost host, Uri home, string pendingFile)
    {
        _node = node; _home = home; _pendingFile = pendingFile;
        Text = "ClipFx Share";
        using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico")) if (s != null) Icon = new Icon(s);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 780);
        MinimumSize = new Size(900, 600);
        bool dark = SystemDark();
        BackColor = dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(242, 242, 243);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open ClipFx Share", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray = new NotifyIcon { Icon = Icon, Text = "ClipFx Share", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => { if (_lastNotifyPath != null) host.Reveal(_lastNotifyPath); else ShowWindow(); };

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
            if (MessageBox.Show("Microsoft Edge WebView2 Runtime is required. Download it now?", "ClipFx Share", MessageBoxButtons.YesNo) == DialogResult.Yes)
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
            if (!(u.Host == _home.Host && u.Port == _home.Port)) e.Cancel = true;
        };
        core.WebMessageReceived += OnMessage;
        core.NavigationCompleted += (_, _) => DeliverPending();
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
                    // Dropped files arrive as objects carrying real paths.
                    var paths = e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).ToArray();
                    AddPaths(paths);
                    break;
                case "attention":
                    Attention(null);
                    break;
            }
        }
        catch { }
    }

    void AddPaths(IEnumerable<string> paths)
    {
        var list = paths.ToArray();
        if (list.Length == 0 || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.ExecuteScriptAsync($"window.cfs && window.cfs.addPaths({JsonSerializer.Serialize(list)})");
    }

    void DeliverPending()
    {
        try
        {
            if (!File.Exists(_pendingFile)) return;
            var paths = File.ReadAllLines(_pendingFile).Where(p => p.Length > 0).Distinct().ToArray();
            File.Delete(_pendingFile);
            AddPaths(paths);
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

    public void Attention(string? balloon)
    {
        ShowWindow();
        FlashWindow(Handle, true);
        if (balloon != null) _tray.ShowBalloonTip(5000, "ClipFx Share", balloon, ToolTipIcon.None);
    }

    public void Notify(string text, string? path)
    {
        _lastNotifyPath = path;
        if (!Visible || WindowState == FormWindowState.Minimized || !ContainsFocus)
            _tray.ShowBalloonTip(4000, "ClipFx Share", text, ToolTipIcon.None);
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_quitting && e.CloseReason == CloseReason.UserClosing && _node.Settings.CloseToTray)
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
    [DllImport("user32.dll")] static extern bool FlashWindow(IntPtr hwnd, bool invert);

    void SetDarkTitleBar(bool dark)
    {
        if (!IsHandleCreated) return;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int));
    }
}
