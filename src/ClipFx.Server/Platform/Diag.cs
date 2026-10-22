using System.Collections.Concurrent;

namespace ClipFx.Platform;

/// <summary>Lightweight counters and last-seen notes for troubleshooting (GET /api/diag).</summary>
public static class Diag
{
    static readonly ConcurrentDictionary<string, long> Counters = new();
    static readonly ConcurrentDictionary<string, string> Notes = new();

    public static void Count(string name, long by = 1) => Counters.AddOrUpdate(name, by, (_, v) => v + by);
    public static void Note(string name, string value) => Notes[name] = value.Length > 400 ? value[..400] : value;

    public static object Snapshot() => new
    {
        counters = new SortedDictionary<string, long>(Counters),
        notes = new SortedDictionary<string, string>(Notes),
    };
}
