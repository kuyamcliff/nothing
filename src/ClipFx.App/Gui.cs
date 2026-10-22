using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClipFx.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace ClipFx.App;

static class Gui
{
    const string InstanceEvent = "Local\\ClipFxManager.Activate";

    public static int Run()
    {
        // Single instance: a second launch brings the first window forward.
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceEvent, out bool first);
        if (!first)
        {
            activate.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        var waiter = new Thread(() =>
        {
            while (activate.WaitOne())
                form.BeginInvoke(form.BringToFront2);
        }) { IsBackground = true };
        waiter.Start();
        Application.Run(form);
        return 0;
    }
}

sealed class MainForm : Form
{
    static readonly Uri Home = new($"http://127.0.0.1:{ServerOptions.DefaultPort}/");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1.5) };

    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _retry = new() { Interval = 1500 };
    bool _offline;

    public MainForm()
    {
        Text = "ClipFx Manager";
        using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("clipfx.ico"))
            if (s != null) Icon = new Icon(s);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 780);
        MinimumSize = new Size(860, 560);
        bool dark = SystemPrefersDark();
        BackColor = dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(243, 243, 244);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        _retry.Tick += async (_, _) => await TryReconnect();
        HandleCreated += (_, _) => SetDarkTitleBar(dark);
        Load += async (_, _) => await InitAsync();
    }

    public void BringToFront2()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipFx Manager", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (MessageBox.Show(this, "Microsoft Edge WebView2 Runtime is required. Download it now?", "ClipFx Manager",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = Debugger.IsAttached;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) =>
        {
            var uri = new Uri(e.Uri);
            bool ours = uri.Scheme == "data" || uri.Scheme == "about" || (uri.Host == Home.Host && uri.Port == Home.Port);
            if (!ours) e.Cancel = true;
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess && !_offline) ShowOffline();
        };
        core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        core.Navigate(Home.ToString());
    }

    void OnMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "theme":
                    bool dark = root.GetProperty("dark").GetBoolean();
                    SetDarkTitleBar(dark);
                    BackColor = dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(243, 243, 244);
                    break;
                case "start-service":
                    StartServiceElevated();
                    break;
            }
        }
        catch { }
    }

    void ShowOffline()
    {
        _offline = true;
        _web.CoreWebView2.NavigateToString(OfflinePage.Html(SystemPrefersDark()));
        _retry.Start();
    }

    async Task TryReconnect()
    {
        try
        {
            using var r = await Http.GetAsync(new Uri(Home, "api/snapshot"));
            if (!r.IsSuccessStatusCode) return;
        }
        catch { return; }
        _retry.Stop();
        _offline = false;
        _web.CoreWebView2.Navigate(Home.ToString());
    }

    static void StartServiceElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--start-service")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { /* user cancelled the elevation prompt */ }
    }

    static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void SetDarkTitleBar(bool dark)
    {
        if (!IsHandleCreated) return;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int));
    }
}
