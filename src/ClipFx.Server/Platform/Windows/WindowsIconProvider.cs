using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using ClipFx.Model;

namespace ClipFx.Platform.Windows;

public sealed class WindowsIconProvider : IIconProvider
{
    readonly ConcurrentDictionary<string, byte[]?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public byte[]? GetPng(AppIdentity identity)
    {
        if (identity.Kind is AppKind.Service or AppKind.System or AppKind.Unknown || identity.Path is null) return null;
        return _cache.GetOrAdd(identity.Path, Extract);
    }

    static byte[]? Extract(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
