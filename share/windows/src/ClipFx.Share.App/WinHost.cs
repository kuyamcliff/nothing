using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipFx.Share.Web;

namespace ClipFx.Share.App;

/// <summary>Windows integration: pickers, Explorer, clipboard, notifications, keeping the PC awake.</summary>
sealed class WinHost : IHostActions, IShareHost
{
    MainForm? _form;
    ShareNode? _node;

    public void Attach(MainForm form, ShareNode node) { _form = form; _node = node; }

    T OnUi<T>(Func<T> f) => _form!.InvokeRequired ? (T)_form.Invoke(f) : f();

    public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult(OnUi<IReadOnlyList<string>>(() =>
    {
        using var d = new OpenFileDialog { Multiselect = true, Title = "Choose files to send" };
        return d.ShowDialog(_form) == DialogResult.OK ? d.FileNames : [];
    }));

    public Task<string?> PickFolderAsync(string? title = null) => Task.FromResult(OnUi<string?>(() =>
    {
        using var d = new FolderBrowserDialog { Description = title ?? "Choose a folder to send", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        return d.ShowDialog(_form) == DialogResult.OK ? d.SelectedPath : null;
    }));

    public void Open(string path) => Try(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));

    public void Reveal(string path) => Try(() =>
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else Process.Start(new ProcessStartInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!) { UseShellExecute = true });
    });

    public void CopyText(string text) => OnUi(() => { Clipboard.SetText(text); return 0; });

    public bool StartWithWindows { get => Startup.Get(); set => Startup.Set(value); }

    // ---------------------------------------------------------------- IShareHost

    public void IncomingRequest(TransferView t) => _form?.BeginInvoke(() => _form.Attention($"{t.Peer.Name} wants to send {t.Title}"));

    public void TransferFinished(TransferView t)
    {
        if (_form is null) return;
        string verb = t.Direction == Direction.Receive ? "Received" : "Sent";
        string msg = t.State == TransferState.Completed ? $"{verb} {t.Title}" : $"Transfer failed: {t.Error}";
        _form.BeginInvoke(() => _form.Notify(msg, t.OpenPath));
        if (t.State == TransferState.Completed && t.Direction == Direction.Receive && _node?.Settings.OpenFolderWhenDone == true && t.OpenPath != null)
            Reveal(t.OpenPath);
    }

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
    readonly object _awakeGate = new();
    Thread? _awakeThread;
    volatile bool _awake;

    /// <summary>Stops the PC from sleeping mid-transfer (execution state is per thread, so one thread holds it).</summary>
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

    static void Try(Action a) { try { a(); } catch { } }
}
