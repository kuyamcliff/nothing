using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClipFx.Model;

namespace ClipFx.Platform.Windows;

public sealed class WindowsProcessResolver : IProcessResolver
{
    sealed record Entry(AppIdentity Identity, DateTime Expires);

    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    static readonly TimeSpan AfterExit = TimeSpan.FromSeconds(15);
    static readonly string WinDir = (Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows").TrimEnd('\\') + "\\";
    static readonly string[] SystemPrefixes =
    [
        WinDir,
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + @"\Microsoft\Windows Defender\",
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Windows Defender\",
    ];

    readonly ConcurrentDictionary<int, Entry> _cache = new();
    readonly ConcurrentDictionary<int, string> _hints = new();
    readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    readonly object _svcGate = new();
    Dictionary<int, List<(string Name, string Display)>> _services = new();
    DateTime _servicesAt;
    Dictionary<string, string>? _devices;

    public AppIdentity? Resolve(int pid)
    {
        if (pid is 0 or 4) return AppIdentity.Kernel;
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(pid, out var e) && e.Expires > now) return e.Identity;

        var id = ResolveLive(pid);
        if (id == null && _hints.TryGetValue(pid, out var hinted)) id = FromPath(pid, hinted, IntPtr.Zero);
        if (id != null) _cache[pid] = new Entry(id, now + CacheFor);
        return id;
    }

    public void Hint(int pid, string imagePath)
    {
        var path = DeviceToDosPath(imagePath);
        if (path == null) return;
        _hints[pid] = path;
        _cache.TryRemove(pid, out _); // new process may reuse an old pid
    }

    public void Exited(int pid)
    {
        if (_cache.TryGetValue(pid, out var e)) _cache[pid] = e with { Expires = DateTime.UtcNow + AfterExit };
        _ = Task.Delay(AfterExit).ContinueWith(_ => _hints.TryRemove(pid, out string? _));
    }

    public IReadOnlyList<int> FindPids(AppIdentity identity)
    {
        var result = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { if (Resolve(p.Id)?.Key == identity.Key) result.Add(p.Id); }
                catch { }
            }
        }
        return result;
    }

    AppIdentity? ResolveLive(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!Native.QueryFullProcessImageNameW(h, 0, sb, ref size)) return null;
            return FromPath(pid, sb.ToString(), h);
        }
        finally { Native.CloseHandle(h); }
    }

    AppIdentity FromPath(int pid, string path, IntPtr process)
    {
        bool isSystemPath = SystemPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        string file = Path.GetFileName(path);

        if (file.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) && isSystemPath)
        {
            var svcs = ServicesFor(pid);
            if (svcs.Count > 0)
            {
                var names = svcs.Select(s => s.Name).ToList();
                string display = svcs.Count == 1 ? svcs[0].Display : string.Join(", ", svcs.Select(s => s.Display));
                return new AppIdentity(AppIdentity.ServiceKey(names), AppKind.Service, display, path, names, null, null, true);
            }
        }

        if (process != IntPtr.Zero && PackageFamily(process) is string family)
        {
            return new AppIdentity(AppIdentity.PackageKey(family), AppKind.Packaged, DisplayName(path), path, [],
                family, PackageSid(family), isSystemPath);
        }

        return new AppIdentity(AppIdentity.ProgramKey(path), AppKind.Program, DisplayName(path), path, [], null, null, isSystemPath);
    }

    string DisplayName(string path) => _names.GetOrAdd(path, p =>
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(p);
            var name = info.FileDescription?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 80) name = info.ProductName?.Trim();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(p);
    });

    static string? PackageFamily(IntPtr process)
    {
        uint len = 0;
        int rc = Native.GetPackageFamilyName(process, ref len, null);
        if (rc != Native.ERROR_INSUFFICIENT_BUFFER || len == 0) return null;
        var sb = new StringBuilder((int)len);
        return Native.GetPackageFamilyName(process, ref len, sb) == 0 ? sb.ToString() : null;
    }

    static string? PackageSid(string family)
    {
        if (Native.DeriveAppContainerSidFromAppContainerName(family, out var sid) != 0 || sid == IntPtr.Zero) return null;
        try
        {
            if (!Native.ConvertSidToStringSidW(sid, out var str)) return null;
            try { return Marshal.PtrToStringUni(str); }
            finally { Native.LocalFree(str); }
        }
        finally { Native.FreeSid(sid); }
    }

    List<(string Name, string Display)> ServicesFor(int pid)
    {
        lock (_svcGate)
        {
            if (_services.TryGetValue(pid, out var list) && DateTime.UtcNow - _servicesAt < TimeSpan.FromSeconds(30)) return list;
            if (DateTime.UtcNow - _servicesAt > TimeSpan.FromSeconds(2))
            {
                try { _services = EnumerateServices(); _servicesAt = DateTime.UtcNow; } catch { }
            }
            return _services.TryGetValue(pid, out list) ? list : [];
        }
    }

    static Dictionary<int, List<(string, string)>> EnumerateServices()
    {
        var result = new Dictionary<int, List<(string, string)>>();
        var scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return result;
        try
        {
            uint resume = 0;
            Native.EnumServicesStatusExW(scm, Native.SC_ENUM_PROCESS_INFO, Native.SERVICE_WIN32, Native.SERVICE_ACTIVE,
                IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
            var buf = Marshal.AllocHGlobal((int)needed + 4096);
            try
            {
                resume = 0;
                if (!Native.EnumServicesStatusExW(scm, Native.SC_ENUM_PROCESS_INFO, Native.SERVICE_WIN32, Native.SERVICE_ACTIVE,
                        buf, needed + 4096, out _, out uint count, ref resume, null)) return result;
                int stride = Marshal.SizeOf<Native.ENUM_SERVICE_STATUS_PROCESS>();
                for (int i = 0; i < count; i++)
                {
                    var s = Marshal.PtrToStructure<Native.ENUM_SERVICE_STATUS_PROCESS>(buf + i * stride);
                    int spid = (int)s.ProcessId;
                    if (spid == 0) continue;
                    var name = Marshal.PtrToStringUni(s.ServiceName) ?? "";
                    var display = Marshal.PtrToStringUni(s.DisplayName) ?? name;
                    if (!result.TryGetValue(spid, out var l)) result[spid] = l = new();
                    l.Add((name, display));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Native.CloseServiceHandle(scm); }
        return result;
    }

    /// <summary>Converts \Device\HarddiskVolume3\x.exe to C:\x.exe.</summary>
    string? DeviceToDosPath(string path)
    {
        if (path.Length > 2 && path[1] == ':') return path;
        _devices ??= LoadDevices();
        foreach (var (device, drive) in _devices)
            if (path.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
                return drive + path[device.Length..];
        _devices = LoadDevices();
        return null;
    }

    static Dictionary<string, string> LoadDevices()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(512);
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string drive = c + ":";
            if (Native.QueryDosDeviceW(drive, sb, sb.Capacity) != 0) map[sb.ToString()] = drive;
        }
        return map;
    }
}
