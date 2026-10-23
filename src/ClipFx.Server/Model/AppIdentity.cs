using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ClipFx.Model;

[JsonConverter(typeof(JsonStringEnumConverter<AppKind>))]
public enum AppKind { Program, Service, System, Packaged, Unknown, Network, Virtual }

/// <summary>Who is using the network. Key is stable across restarts.</summary>
public sealed record AppIdentity(
    string Key,
    AppKind Kind,
    string Name,
    string? Path,
    IReadOnlyList<string> Services,
    string? PackageFamily,
    string? PackageSid,
    bool IsSystem)
{
    public const string SystemKey = "sys";
    public const string UnknownKey = "unknown";

    public static AppIdentity Kernel { get; } =
        new(SystemKey, AppKind.System, "System", null, [], null, null, true);

    public static AppIdentity Unknown { get; } =
        new(UnknownKey, AppKind.Unknown, "Unknown process", null, [], null, null, false);

    /// <summary>Bytes the network adapter carried that no process accounts for.</summary>
    public static AppIdentity OtherTraffic { get; } =
        new("net:other", AppKind.Network, "Other network traffic", null, [], null, null, false);

    /// <summary>Traffic from WSL, Docker and Hyper-V virtual machines.</summary>
    public static AppIdentity VirtualMachines { get; } =
        new("net:vm", AppKind.Virtual, "Virtual machines and WSL", null, [], null, null, false);

    public static string ProgramKey(string path) => "exe:" + path.ToLowerInvariant();
    public static string ServiceKey(IEnumerable<string> services) =>
        "svc:" + string.Join(",", services.Select(s => s.ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal));
    public static string PackageKey(string family) => "pkg:" + family.ToLowerInvariant();

    [JsonIgnore] public string Id => KeyId.Of(Key);

    /// <summary>True when a firewall rule can target this identity.</summary>
    [JsonIgnore]
    public bool CanBlock => Kind switch
    {
        AppKind.System => true,
        AppKind.Service => Services.Count > 0,
        AppKind.Packaged => PackageSid != null || Path != null,
        AppKind.Program => Path != null,
        _ => false,
    };
}

public static class KeyId
{
    public static string Of(string key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }
}
