using System.Text;

namespace ClipFx.Share;

/// <summary>Turns peer-supplied relative paths into safe local paths.</summary>
public static class PathSafety
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    const string Invalid = "<>:\"|?*\\";

    public static string CleanSegment(string seg)
    {
        var sb = new StringBuilder(seg.Length);
        foreach (char c in seg) sb.Append(c < 32 || Invalid.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length == 0 || s == "." || s == "..") s = "_";
        var stem = s.Split('.')[0];
        if (Reserved.Contains(stem)) s = "_" + s;
        if (Encoding.UTF8.GetByteCount(s) > 240) s = s[..Math.Min(s.Length, 120)];
        return s;
    }

    /// <summary>Relative path -> cleaned segments. Never escapes the root.</summary>
    public static string[] Segments(string relative) =>
        relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != "..").Select(CleanSegment).DefaultIfEmpty("_").ToArray();

    public static string Combine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine([root, .. Segments(relative)]));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Path escapes the save folder.");
        return full;
    }

    /// <summary>"name.ext" -> "name (2).ext" until it doesn't exist.</summary>
    public static string Unique(string path, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (!exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!exists(p)) return p;
        }
    }
}
