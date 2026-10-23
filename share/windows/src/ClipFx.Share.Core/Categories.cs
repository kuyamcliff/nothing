namespace ClipFx.Share;

/// <summary>Sorts received files into folders by type.</summary>
public static class Categories
{
    public const string Folders = "Folders";

    static readonly (string Name, string[] Ext)[] Map =
    [
        ("Videos", ["mp4", "mkv", "mov", "avi", "wmv", "flv", "webm", "m4v", "3gp", "3g2", "ts", "mts", "m2ts", "mpeg", "mpg", "vob", "ogv", "rmvb"]),
        ("Images", ["jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "bmp", "tif", "tiff", "svg", "ico", "avif", "raw", "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "psd"]),
        ("Music", ["mp3", "m4a", "aac", "flac", "wav", "ogg", "oga", "opus", "wma", "amr", "mid", "midi", "aiff", "alac", "ape"]),
        ("Documents", ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "rtf", "csv", "md", "epub", "mobi", "pages", "numbers", "key", "json", "xml", "html", "htm", "log"]),
        ("Apps", ["apk", "apks", "xapk", "apkm", "aab", "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "dmg", "pkg", "deb", "rpm", "appimage", "jar", "bat", "cmd"]),
        ("Archives", ["zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "zst", "lz", "lzma", "cab", "iso", "img", "z", "arj"]),
    ];

    static readonly Dictionary<string, string> ByExt =
        Map.SelectMany(m => m.Ext.Select(e => (e, m.Name))).ToDictionary(x => x.e, x => x.Name, StringComparer.OrdinalIgnoreCase);

    public static string Of(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        return ByExt.TryGetValue(ext, out var c) ? c : "Other";
    }

    /// <summary>
    /// Where a received item goes. Single files go to their type folder; anything inside
    /// a sent folder stays together under Folders so its structure is kept.
    /// </summary>
    public static string Place(string relativePath, ItemType type, bool organize)
    {
        if (!organize || type == ItemType.Text) return relativePath;
        var normalized = relativePath.Replace('\\', '/');
        bool insideFolder = normalized.Contains('/') || type == ItemType.Dir;
        return insideFolder ? $"{Folders}/{normalized}" : $"{Of(normalized)}/{normalized}";
    }
}
