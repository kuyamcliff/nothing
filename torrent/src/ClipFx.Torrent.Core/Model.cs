using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipFx.Torrents;

public sealed class Settings
{
    public string SaveDir { get; set; } = DefaultSaveDir();
    public string? MoveDoneTo { get; set; }
    public bool ShowAddDialog { get; set; } = true;
    public bool SequentialByDefault { get; set; }
    public int ActiveDownloads { get; set; } = 5;       // 0 = no limit

    public int DownloadLimitKB { get; set; }            // 0 = unlimited
    public int UploadLimitKB { get; set; }
    public int ConnectionsPerTorrent { get; set; } = 250;
    public int ConnectionsTotal { get; set; } = 1000;
    public int HalfOpen { get; set; } = 80;
    public int UploadSlots { get; set; } = 8;
    public int DiskCacheMB { get; set; } = 128;

    public int Port { get; set; }
    public bool PortForwarding { get; set; } = true;
    public bool Dht { get; set; } = true;
    public bool PeerExchange { get; set; } = true;
    public bool LocalPeers { get; set; } = true;
    public bool ExtraTrackers { get; set; } = true;
    public string CustomTrackers { get; set; } = "";
    public string Encryption { get; set; } = "prefer";   // prefer | require | off

    public string Seeding { get; set; } = "ratio";       // forever | ratio | stop
    public double SeedRatio { get; set; } = 1.0;

    public bool KeepAwake { get; set; } = true;
    public bool Notify { get; set; } = true;
    public bool OpenFolderWhenDone { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public string Theme { get; set; } = "system";

    public static string DefaultSaveDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Path.GetTempPath();
        return Path.Combine(home, "Downloads");
    }
}

/// <summary>A torrent the user added; survives restarts.</summary>
public sealed class Entry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Magnet { get; set; } = "";
    public string SaveDir { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public bool Paused { get; set; }
    public bool Sequential { get; set; }
    public bool Forced { get; set; }
    public bool SeedDone { get; set; }          // finished seeding by policy
    public int Order { get; set; }
    public List<int>? Skip { get; set; }        // file indexes not to download (applied once metadata exists)
    public Dictionary<int, string>? Priorities { get; set; }
    public long Downloaded { get; set; }
    public long Uploaded { get; set; }
    public long Size { get; set; }
}

public enum Status { Metadata, Checking, Queued, Downloading, Stalled, Seeding, Finished, Paused, Error, Moving }

public sealed record TorrentView(
    string Id, string Name, Status Status, double Progress, long Size, long Wanted, long Done,
    long Down, long Up, double? Eta, int Peers, int Seeds, int PeersTotal, int SeedsTotal, double Ratio,
    long Downloaded, long Uploaded, string SavePath, DateTime AddedAt, DateTime? CompletedAt,
    bool Sequential, bool Forced, bool HasMetadata, string? Error, int Order, int Files, string? Open, string Kind, bool Private);

public sealed record FileView(int Index, string Path, long Size, double Progress, string Priority, string FullPath);
public sealed record PeerView(string Address, string Client, long Down, long Up, double Progress, bool Seed, bool Incoming, bool Encrypted, string Flags);
public sealed record TrackerView(string Url, string Status, int Seeds, int Peers, int Downloaded, string? Message);

public sealed record DetailView(
    string Id, string Hash, string Magnet, string? Comment, string? CreatedBy, DateTime? Created, int PieceLength, int Pieces,
    IReadOnlyList<FileView> Files, IReadOnlyList<PeerView> Peers, IReadOnlyList<TrackerView> Trackers, int[] PieceMap);

public sealed record GlobalView(long Down, long Up, int Connections, int HalfOpen, int DhtNodes, string DhtState, int Port, string PortStatus, long FreeSpace, bool Online);

public sealed record PreviewFile(int Index, string Path, long Size);
public sealed record Preview(string Id, string Name, long Size, bool HasMetadata, bool Private, int Trackers, IReadOnlyList<PreviewFile> Files, string? Existing);

static class Store
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try { if (File.Exists(p)) return JsonSerializer.Deserialize<T>(File.ReadAllText(p), Json) ?? fallback(); }
            catch { }
        }
        return fallback();
    }

    /// <summary>Atomic write: never leaves a half-written file if the PC loses power.</summary>
    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
        else File.Move(tmp, path);
    }
}
