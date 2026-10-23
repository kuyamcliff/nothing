using System.Text.Json;

namespace ClipFx.Share;

/// <summary>Small crash-safe JSON files.</summary>
public static class Store
{
    public static T? Load<T>(string path) where T : class
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try { if (File.Exists(p)) return JsonSerializer.Deserialize<T>(File.ReadAllBytes(p), Json.Options); }
            catch { }
        }
        return null;
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
        else File.Move(tmp, path);
    }

    public static void Delete(string path)
    {
        foreach (var p in new[] { path, path + ".bak", path + ".tmp" })
            try { File.Delete(p); } catch { }
    }
}
