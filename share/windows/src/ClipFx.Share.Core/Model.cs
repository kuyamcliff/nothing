using System.Text.Json.Serialization;

namespace ClipFx.Share;

public static class Proto
{
    public const int Version = 1;
    public const int TcpPort = 47630;
    public const int UdpPort = 47631;
    public const string MulticastGroup = "239.255.47.63";
}

public sealed record DeviceInfo(string Id, string Name, string Platform, int Port);

public enum ItemType { File, Dir, Text }

/// <summary>One entry of a transfer. Path is relative, '/'-separated.</summary>
public sealed record ItemInfo(int I, string Path, long Size, long Mtime, ItemType Type, string? Text = null);

[JsonConverter(typeof(JsonStringEnumConverter<TransferState>))]
public enum TransferState
{
    Waiting,       // waiting for the receiver to accept (sender) / for the user to decide (receiver)
    Connecting,
    Active,
    Paused,        // paused by this side
    PausedByPeer,
    Reconnecting,
    Completed,
    Declined,
    Cancelled,
    Failed,
    Interrupted,   // stopped by app exit or long disconnect; can be resumed
}

public static class States
{
    public static bool IsFinal(TransferState s) => s is TransferState.Completed or TransferState.Declined or TransferState.Cancelled or TransferState.Failed;
}

public enum Direction { Send, Receive }

public sealed class ShareSettings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    public string SaveDir { get; set; } = DefaultSaveDir();
    public bool AskBeforeReceive { get; set; } = true;
    public List<string> TrustedDevices { get; set; } = [];
    public int Streams { get; set; } = 4;
    public int ChunkMB { get; set; } = 8;
    public bool Verify { get; set; } = true;
    public string Conflict { get; set; } = "rename"; // rename | overwrite | skip
    public string Theme { get; set; } = "system";
    public bool StartWithWindows { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool OpenFolderWhenDone { get; set; }
    public bool Discoverable { get; set; } = true;
    public int Port { get; set; } = Proto.TcpPort;

    public static string DefaultSaveDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(string.IsNullOrEmpty(home) ? Path.GetTempPath() : home, "Downloads", "ClipFx Share");
    }

    public ShareSettings Clone() => (ShareSettings)MemberwiseClone() is var c ? WithLists(c) : null!;
    ShareSettings WithLists(ShareSettings c) { c.TrustedDevices = [.. TrustedDevices]; return c; }
}
