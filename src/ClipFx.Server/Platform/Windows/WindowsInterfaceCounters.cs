using System.Runtime.InteropServices;

namespace ClipFx.Platform.Windows;

/// <summary>Reads byte counters of every network adapter (GetIfTable2).</summary>
public sealed class WindowsInterfaceCounters : IInterfaceCounters
{
    const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    const byte FlagHardware = 0x01, FlagFilter = 0x02;

    public IReadOnlyList<InterfaceSample> Read()
    {
        var list = new List<InterfaceSample>();
        if (IfNative.GetIfTable2(out var table) != 0 || table == IntPtr.Zero) return list;
        try
        {
            int count = Marshal.ReadInt32(table);
            int stride = Marshal.SizeOf<IfNative.MIB_IF_ROW2>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<IfNative.MIB_IF_ROW2>(table + 8 + i * stride);
                var kind = Classify(row);
                if (kind is null) continue;
                list.Add(new InterfaceSample(row.InterfaceLuid.ToString(), kind.Value, (long)row.InOctets, (long)row.OutOctets));
            }
        }
        finally { IfNative.FreeMibTable(table); }
        return list;
    }

    internal static InterfaceKind? Classify(IfNative.MIB_IF_ROW2 row)
    {
        if (row.Type == IF_TYPE_SOFTWARE_LOOPBACK) return null;
        if ((row.Flags & FlagFilter) != 0) return null;          // driver filter layers repeat the adapter below them
        if ((row.Flags & FlagHardware) != 0) return InterfaceKind.Physical;
        bool vSwitch = row.Description.Contains("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase);
        if (vSwitch && !row.Alias.Contains("External", StringComparison.OrdinalIgnoreCase)) return InterfaceKind.VirtualMachine;
        return null; // VPN tunnels and other software adapters ride on a physical adapter that is already counted
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
