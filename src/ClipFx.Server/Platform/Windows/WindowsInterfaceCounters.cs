using System.Runtime.InteropServices;

namespace ClipFx.Platform.Windows;

/// <summary>Reads byte counters of every network adapter (GetIfTable2).</summary>
public sealed class WindowsInterfaceCounters : IInterfaceCounters
{
    const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    const byte FlagHardware = 0x01, FlagFilter = 0x02;
    static readonly TimeSpan GatewayRefresh = TimeSpan.FromSeconds(5);

    HashSet<Guid> _withGateway = new();
    DateTime _gatewaysAt = DateTime.MinValue;
    string _lastSelection = "";

    /// <summary>
    /// Adapters that route to the internet. Bonded or teamed parts of one connection
    /// (for example an SR-IOV virtual function under a synthetic NIC) have no gateway
    /// of their own, so counting only these avoids counting the same bytes twice.
    /// </summary>
    HashSet<Guid> InternetFacing()
    {
        if (DateTime.UtcNow - _gatewaysAt < GatewayRefresh) return _withGateway;
        var set = new HashSet<Guid>();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any))
                    && Guid.TryParse(nic.Id, out var id))
                    set.Add(id);
            }
            catch { }
        }
        _withGateway = set;
        _gatewaysAt = DateTime.UtcNow;
        return set;
    }

    public IReadOnlyList<InterfaceSample> Read()
    {
        var internet = InternetFacing();
        var chosen = new List<string>();
        var list = new List<InterfaceSample>();
        if (IfNative.GetIfTable2(out var table) != 0 || table == IntPtr.Zero) return list;
        try
        {
            int count = Marshal.ReadInt32(table);
            int stride = Marshal.SizeOf<IfNative.MIB_IF_ROW2>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<IfNative.MIB_IF_ROW2>(table + 8 + i * stride);
                var kind = Classify(row, internet.Contains(row.InterfaceGuid));
                if (kind is null) continue;
                chosen.Add($"{kind}:{row.Alias} ({row.Description})");
                list.Add(new InterfaceSample(row.InterfaceLuid.ToString(), kind.Value, (long)row.InOctets, (long)row.OutOctets));
            }
        }
        finally { IfNative.FreeMibTable(table); }
        var selection = string.Join("; ", chosen);
        if (selection != _lastSelection) { _lastSelection = selection; Diag.Note("counters.adapters", selection); }
        return list;
    }

    internal static InterfaceKind? Classify(IfNative.MIB_IF_ROW2 row, bool hasGateway)
    {
        if (row.Type == IF_TYPE_SOFTWARE_LOOPBACK) return null;
        if ((row.Flags & FlagFilter) != 0) return null;          // driver filter layers repeat the adapter below them
        bool hardware = (row.Flags & FlagHardware) != 0;
        bool vSwitch = row.Description.Contains("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase);
        if (hasGateway && (hardware || vSwitch)) return InterfaceKind.Physical; // the PC's own way out
        if (vSwitch && !hasGateway) return InterfaceKind.VirtualMachine;        // WSL, Docker, Hyper-V NAT switches
        return null; // VPN tunnels and bonded parts ride on an adapter that is already counted
    }
}

internal static class IfNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte Flags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos, InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors, OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }

    [DllImport("iphlpapi.dll")]
    public static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    public static extern void FreeMibTable(IntPtr memory);
}
