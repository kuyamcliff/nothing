using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipFx.Torrents.Web;

namespace ClipFx.Torrents.App;

/// <summary>Windows integration: pickers, Explorer, clipboard, notifications, power.</summary>
sealed class WinHost : IHostActions, ITorrentHost
{
    MainForm? _form;
    TorrentClient? _client;

    public void Attach(TorrentClient client) => _client = client;
    public void Attach(MainForm form) => _form = form;

    T OnUi<T>(Func<T> f) => _form!.InvokeRequired ? (T)_form.Invoke(f) : f();

    public Task<IReadOnlyList<string>> PickTorrentsAsync() => Task.FromResult(OnUi<IReadOnlyList<string>>(() =>
    {
        using var d = new OpenFileDialog { Multiselect = true, Title = "Open torrent files", Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*" };
        return d.ShowDialog(_form) == DialogResult.OK ? d.FileNames : [];
    }));

    public Task<string?> PickFolderAsync(string? title = null) => Task.FromResult(OnUi<string?>(() =>
    {
        using var d = new FolderBrowserDialog { Description = title ?? "Choose a folder", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        return d.ShowDialog(_form) == DialogResult.OK ? d.SelectedPath : null;
    }));

    public void Open(string path) => Try(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));

    public void Reveal(string path) => Try(() =>
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
        {
            var dir = Path.GetDirectoryName(path);
            while (dir != null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (dir != null) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
    });

    public void CopyText(string text) => OnUi(() => { Clipboard.SetText(text); return 0; });
    public string? ReadClipboard() => OnUi(() => { try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { return null; } });

    public bool StartWithWindows { get => Startup.Get(); set => Startup.Set(value); }
    public bool IsDefaultApp => Associations.IsDefault();

    public void MakeDefaultApp()
    {
        Associations.Register();
        // Windows 10/11 only let the user change protocol defaults, so open the right page if needed.
        if (!Associations.IsDefault())
            Try(() => Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=ClipFx%20Torrent") { UseShellExecute = true }));
    }

    // ---------------------------------------------------------------- ITorrentHost

    public void Finished(TorrentView t)
    {
        if (_form is null || _client is null) return;
        var path = t.Open ?? t.SavePath;
        if (_client.Settings.Notify) _form.BeginInvoke(() => _form.Notify($"Downloaded {t.Name}", path));
        if (_client.Settings.OpenFolderWhenDone) Reveal(path);
    }

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
    readonly object _awakeGate = new();
    Thread? _awakeThread;
    volatile bool _awake;

    /// <summary>Stops the PC from sleeping mid-download (execution state is per thread, so one thread holds it).</summary>
    public void KeepAwake(bool active)
    {
        lock (_awakeGate)
        {
            _awake = active;
            if (active && _awakeThread is null)
            {
                _awakeThread = new Thread(() =>
                {
                    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                    while (_awake) Thread.Sleep(1000);
                    SetThreadExecutionState(ES_CONTINUOUS);
                    lock (_awakeGate) _awakeThread = null;
                }) { IsBackground = true, Name = "keep-awake" };
                _awakeThread.Start();
            }
        }
    }

    public void AllDone(string action) => _form?.BeginInvoke(() => _form.Countdown(action));

    public static void RunPowerAction(string action)
    {
        switch (action)
        {
            case "sleep": Application.SetSuspendState(PowerState.Suspend, false, false); break;
            case "hibernate": Application.SetSuspendState(PowerState.Hibernate, false, false); break;
            case "shutdown": Try(() => Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false })); break;
        }
    }

    static void Try(Action a) { try { a(); } catch { } }
}
