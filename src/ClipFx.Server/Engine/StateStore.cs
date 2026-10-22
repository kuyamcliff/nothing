using System.Text.Json;
using System.Text.Json.Serialization;
using ClipFx.Model;

namespace ClipFx.Engine;

/// <summary>Crash-safe JSON persistence: write temp file, flush to disk, then atomically replace.</summary>
public sealed class StateStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly string _path;
    readonly string _backup;
    readonly object _io = new();

    public StateStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "state.json");
        _backup = Path.Combine(dataDir, "state.bak.json");
    }

    public string FilePath => _path;

    public static byte[] Serialize(PersistedState s) => JsonSerializer.SerializeToUtf8Bytes(s, Json);

    public PersistedState Load()
    {
        foreach (var file in new[] { _path, _backup })
        {
            try
            {
                if (!File.Exists(file)) continue;
                var s = JsonSerializer.Deserialize<PersistedState>(File.ReadAllBytes(file), Json);
                if (s is null) continue;
                s.Apps = new Dictionary<string, AppRecord>(
                    s.Apps.Where(kv => kv.Value?.Identity != null), StringComparer.Ordinal);
                s.Settings ??= new Settings();
                return s;
            }
            catch (Exception)
            {
                try { File.Copy(file, file + ".corrupt", overwrite: true); } catch { }
            }
        }
        return new PersistedState();
    }

    public void Save(byte[] bytes)
    {
        lock (_io)
        {
            var tmp = _path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Replace(tmp, _path, _backup, ignoreMetadataErrors: true);
            else File.Move(tmp, _path);
        }
    }
}
