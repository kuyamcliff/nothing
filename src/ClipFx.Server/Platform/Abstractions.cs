using System.Net;
using ClipFx.Model;

namespace ClipFx.Platform;

public readonly record struct TrafficEvent(int Pid, long Bytes, bool Sent, IPAddress? Remote);

public interface ITrafficSource : IDisposable
{
    event Action<TrafficEvent>? Traffic;
    event Action<int, string>? ProcessStarted;
    event Action<int>? ProcessStopped;
    void Start();
    bool IsRunning { get; }
    string? Error { get; }
}

public interface IProcessResolver
{
    /// <summary>Returns null when the process can't be identified yet.</summary>
    AppIdentity? Resolve(int pid);
    void Hint(int pid, string imagePath);
    void Exited(int pid);
    IReadOnlyList<int> FindPids(AppIdentity identity);
}

public interface IFirewall
{
    public const string GlobalKey = "__all__";
    /// <summary>Keys of identities currently blocked by our rules, mapped to the rule signature.</summary>
    IReadOnlyDictionary<string, string> GetApplied();
    string Signature(AppIdentity identity);
    void Block(AppIdentity identity);
    void Unblock(string key);
    void SetGlobalBlock(bool on);
    bool IsEnabled();
    void Enable();
    void RemoveAll();
}

public interface IConnectionKiller
{
    void Kill(IReadOnlyCollection<int> pids);
    void KillAll();
}

public interface IIconProvider
{
    byte[]? GetPng(AppIdentity identity);
}

public enum InterfaceKind { Physical, VirtualMachine }

/// <summary>Cumulative byte counters of one network adapter.</summary>
public readonly record struct InterfaceSample(string Id, InterfaceKind Kind, long InOctets, long OutOctets);

/// <summary>Reads adapter-level byte counters: every byte on the wire, whoever sent it.</summary>
public interface IInterfaceCounters
{
    IReadOnlyList<InterfaceSample> Read();
}

public sealed record PlatformServices(
    ITrafficSource Traffic,
    IProcessResolver Resolver,
    IFirewall Firewall,
    IConnectionKiller Killer,
    IIconProvider Icons,
    bool Simulated,
    IInterfaceCounters? Counters = null);
