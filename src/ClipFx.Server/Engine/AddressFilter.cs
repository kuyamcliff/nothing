using System.Net;
using System.Net.Sockets;

namespace ClipFx.Engine;

/// <summary>Decides whether traffic to a remote address costs internet data.</summary>
public static class AddressFilter
{
    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    public static bool IsInternet(IPAddress? address)
    {
        if (address is null) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);
            if (b[0] == 0) return false;                                   // 0.0.0.0/8
            if (b[0] == 10) return false;                                  // 10/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;     // 172.16/12
            if (b[0] == 192 && b[1] == 168) return false;                  // 192.168/16
            if (b[0] == 169 && b[1] == 254) return false;                  // link-local
            if (b[0] >= 224) return false;                                 // multicast, broadcast, reserved
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None)) return false;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            Span<byte> b = stackalloc byte[16];
            address.TryWriteBytes(b, out _);
            if ((b[0] & 0xFE) == 0xFC) return false;                       // fc00::/7 unique local
            return true;
        }

        return true;
    }
}
