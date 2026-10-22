using System.Runtime.InteropServices;

namespace ClipFx.Platform.Windows;

/// <summary>Drops established IPv4 TCP connections so a block takes effect on open sockets too.</summary>
public sealed class TcpConnectionKiller : IConnectionKiller
{
    public void Kill(IReadOnlyCollection<int> pids)
    {
        var set = pids.Select(p => (uint)p).ToHashSet();
        Drop(row => set.Contains(row.OwningPid));
    }

    public void KillAll() => Drop(_ => true);

    static void Drop(Func<Native.MIB_TCPROW_OWNER_PID, bool> match)
    {
        foreach (var row in Rows())
        {
            if (row.State == Native.MIB_TCP_STATE_LISTEN) continue;
            if ((row.RemoteAddr & 0xFF) == 127 || (row.LocalAddr & 0xFF) == 127) continue; // keep loopback (our UI)
            if (!match(row)) continue;
            var r = new Native.MIB_TCPROW
            {
                State = Native.MIB_TCP_STATE_DELETE_TCB,
                LocalAddr = row.LocalAddr, LocalPort = row.LocalPort,
                RemoteAddr = row.RemoteAddr, RemotePort = row.RemotePort,
            };
            Native.SetTcpEntry(ref r);
        }
    }

    static List<Native.MIB_TCPROW_OWNER_PID> Rows()
    {
        var list = new List<Native.MIB_TCPROW_OWNER_PID>();
        int size = 0;
        Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, Native.AF_INET, Native.TCP_TABLE_OWNER_PID_ALL, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            size += 4096;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.GetExtendedTcpTable(buf, ref size, false, Native.AF_INET, Native.TCP_TABLE_OWNER_PID_ALL, 0) != 0) continue;
                int count = Marshal.ReadInt32(buf);
                int stride = Marshal.SizeOf<Native.MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                    list.Add(Marshal.PtrToStructure<Native.MIB_TCPROW_OWNER_PID>(buf + 4 + i * stride));
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return list;
    }
}
