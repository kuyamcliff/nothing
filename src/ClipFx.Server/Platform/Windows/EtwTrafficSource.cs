using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ClipFx.Platform.Windows;

/// <summary>
/// Reads per-process network byte counts from the Microsoft-Windows-Kernel-Network ETW provider.
/// Requires administrator / LocalSystem.
/// </summary>
public sealed class EtwTrafficSource : ITrafficSource
{
    const string SessionName = "ClipFxManager-Network";
    static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    static readonly Guid KernelProcess = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    const ulong NetIPv4 = 0x10, NetIPv6 = 0x20, ProcessKeyword = 0x10, ImageKeyword = 0x40;

    // Event id -> sent?   (TCP send/recv/retransmit, UDP send/recv, IPv4 and IPv6)
    static readonly Dictionary<int, bool> NetEvents = new()
    {
        [10] = true, [11] = false, [14] = true,
        [26] = true, [27] = false, [30] = true,
        [42] = true, [43] = false,
        [58] = true, [59] = false,
    };

    volatile bool _disposed;
    bool _loggedNet;
    volatile bool _running;
    TraceEventSession? _session;
    Thread? _thread;

    public event Action<TrafficEvent>? Traffic;
    public event Action<int, string>? ProcessStarted;
    public event Action<int>? ProcessStopped;
    /// <summary>A process started but its path isn't known yet.</summary>
    public event Action<int>? ProcessSeen;

    readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _awaitingExe = new();

    void Started(int pid, string image)
    {
        _awaitingExe.TryRemove(pid, out _);
        ProcessStarted?.Invoke(pid, image);
    }

    public bool IsRunning => _running;
    public string? Error { get; private set; }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ClipFx ETW" };
        _thread.Start();
    }

    void Loop()
    {
        while (!_disposed)
        {
            try
            {
                using var session = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 32 };
                _session = session;
                session.EnableProvider(KernelNetwork, TraceEventLevel.Verbose, NetIPv4 | NetIPv6);
                session.EnableProvider(KernelProcess, TraceEventLevel.Verbose, ProcessKeyword | ImageKeyword);
                var parser = new RegisteredTraceEventParser(session.Source);
                parser.All += OnEvent;
                using var flusher = new Timer(_ => { try { session.Flush(); } catch { } }, null, 100, 100);
                _running = true;
                Error = null;
                session.Source.Process();
            }
            catch (Exception ex)
            {
                Diag.Note("etw.sessionError", ex.ToString());
                Error = ex is UnauthorizedAccessException ? "Monitoring needs administrator rights." : ex.Message;
            }
            finally
            {
                _running = false;
                _session = null;
            }
            if (!_disposed) Thread.Sleep(3000);
        }
    }

    void OnEvent(TraceEvent e)
    {
        try
        {
            Diag.Count("etw.events");
            if (e.ProviderGuid == KernelNetwork)
            {
                Diag.Count("etw.net." + (int)e.ID);
                if (!NetEvents.TryGetValue((int)e.ID, out bool sent)) return;
                if (!_loggedNet) { _loggedNet = true; Diag.Note("etw.net.fields", string.Join(",", e.PayloadNames) + " | " + e.ToString()); }
                int pid = Convert.ToInt32(Field(e, "PID") ?? e.ProcessID);
                long size = Convert.ToInt64(Field(e, "size") ?? 0L);
                if (size <= 0) return;
                var daddr = ToAddress(Field(e, "daddr"));
                var saddr = ToAddress(Field(e, "saddr"));
                // Pick the address that is on the internet, whichever side it is reported on.
                var remote = Engine.AddressFilter.IsInternet(daddr) ? daddr : saddr;
                Diag.Count("etw.net.bytes", size);
                Traffic?.Invoke(new TrafficEvent(pid, size, sent, remote));
            }
            else if (e.ProviderGuid == KernelProcess)
            {
                int id = (int)e.ID;
                Diag.Count("etw.proc." + id);
                int pid = Convert.ToInt32(Field(e, "ProcessID") ?? e.ProcessID);
                switch (id)
                {
                    case 1: // process start: the first .exe image it maps is its program file
                        _awaitingExe[pid] = 0;
                        if (Field(e, "ImageName") is string startImage) Started(pid, startImage);
                        else ProcessSeen?.Invoke(pid);
                        break;
                    case 2:
                        _awaitingExe.TryRemove(pid, out _);
                        ProcessStopped?.Invoke(pid);
                        break;
                    case 5: // image load
                        if (_awaitingExe.ContainsKey(pid) && Field(e, "ImageName") is string image
                            && image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            Diag.Note("etw.image.last", $"{pid} {image}");
                            Started(pid, image);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // one malformed event must never stop monitoring
            Diag.Count("etw.errors");
            Diag.Note("etw.lastError", ex.GetType().Name + ": " + ex.Message);
        }
    }

    static object? Field(TraceEvent e, string name)
    {
        var names = e.PayloadNames;
        for (int i = 0; i < names.Length; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return e.PayloadValue(i);
        return null;
    }

    internal static IPAddress? ToAddress(object? v) => v switch
    {
        IPAddress a => a,
        byte[] b when b.Length is 4 or 16 => new IPAddress(b),
        uint u => new IPAddress(u),
        int i => new IPAddress((uint)i),
        string s when IPAddress.TryParse(s, out var p) => p,
        _ => null,
    };

    public void Dispose()
    {
        _disposed = true;
        try { _session?.Stop(); } catch { }
        try { _session?.Dispose(); } catch { }
    }
}
