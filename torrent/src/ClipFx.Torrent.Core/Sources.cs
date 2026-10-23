using System.Net;
using System.Text.RegularExpressions;
using MonoTorrent;
using MonoTorrent.BEncoding;

namespace ClipFx.Torrents;

/// <summary>What the user gave us, normalised: either full metadata or a magnet link.</summary>
public sealed class Source
{
    public Torrent? Torrent { get; init; }
    public byte[]? TorrentBytes { get; init; }
    public MagnetLink? Magnet { get; init; }
    public string Id => (Torrent?.InfoHashes ?? Magnet!.InfoHashes).V1OrV2.ToHex().ToLowerInvariant();
    public InfoHashes Hashes => Torrent?.InfoHashes ?? Magnet!.InfoHashes;
    public string Name => Torrent?.Name ?? Magnet?.Name ?? Id;
}

public static partial class Sources
{
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    }) { Timeout = TimeSpan.FromSeconds(45) };

    [GeneratedRegex("^[0-9a-fA-F]{40}$")] private static partial Regex Hex40();
    [GeneratedRegex("^[A-Za-z2-7]{32}$")] private static partial Regex Base32();
    [GeneratedRegex(@"magnet:\?[^\s""'<>]+", RegexOptions.IgnoreCase)] private static partial Regex MagnetInText();

    /// <summary>Pulls every magnet link out of pasted text (people paste whole pages).</summary>
    public static IReadOnlyList<string> FindLinks(string text)
    {
        var found = MagnetInText().Matches(text).Select(m => m.Value).ToList();
        if (found.Count > 0) return found;
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Length > 0).ToList();
    }

    public static async Task<Source> ResolveAsync(string input, CancellationToken ct = default)
    {
        input = input.Trim().Trim('"');
        if (input.Length == 0) throw new UserError("Paste a magnet link, a .torrent link or a file.");

        if (input.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return FromMagnet(input);
        if (Hex40().IsMatch(input) || Base32().IsMatch(input)) return FromMagnet("magnet:?xt=urn:btih:" + input);

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https") return await FromUrlAsync(uri, ct);
            if (uri.IsFile) return FromBytes(await ReadFileAsync(uri.LocalPath, ct));
        }
        if (File.Exists(input)) return FromBytes(await ReadFileAsync(input, ct));
        throw new UserError("That isn't a magnet link, torrent link or .torrent file.");
    }

    static async Task<byte[]> ReadFileAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length > 64 << 20) throw new UserError("That file is too big to be a .torrent.");
        return await File.ReadAllBytesAsync(path, ct);
    }

    public static Source FromMagnet(string link)
    {
        try { return new Source { Magnet = MagnetLink.Parse(link) }; }
        catch (Exception) { throw new UserError("That magnet link is broken."); }
    }

    public static Source FromBytes(byte[] bytes)
    {
        if (!Torrent.TryLoad(bytes, out var t) || t is null)
        {
            // Some sites serve a magnet link as text instead of a .torrent.
            var text = System.Text.Encoding.UTF8.GetString(bytes).Trim();
            if (text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return FromMagnet(text);
            throw new UserError("That .torrent file is damaged.");
        }
        return new Source { Torrent = t, TorrentBytes = bytes };
    }

    static async Task<Source> FromUrlAsync(Uri uri, CancellationToken ct)
    {
        for (int hop = 0; hop < 8; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("ClipFxTorrent/1.0");
            HttpResponseMessage res;
            try { res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            { throw new UserError("Couldn't download the .torrent file. Check the link and your connection."); }
            using (res)
            {
                if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } loc)
                {
                    var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                    if (next.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)) return FromMagnet(next.OriginalString);
                    uri = next;
                    continue;
                }
                if (!res.IsSuccessStatusCode) throw new UserError($"The site answered {(int)res.StatusCode}. The link may have expired.");
                if (res.Content.Headers.ContentLength > 64 << 20) throw new UserError("That link isn't a .torrent file.");
                var bytes = await res.Content.ReadAsByteArrayAsync(ct);
                return FromBytes(bytes);
            }
        }
        throw new UserError("That link redirects too many times.");
    }

    /// <summary>Turns raw metadata from peers (the info dictionary) into a full .torrent.</summary>
    public static byte[] MetadataToTorrent(ReadOnlyMemory<byte> metadata, MagnetLink magnet)
    {
        if (Torrent.TryLoad(metadata.Span, out _)) return metadata.ToArray();
        var info = BEncodedValue.Decode<BEncodedDictionary>(metadata.Span);
        var dict = new BEncodedDictionary { ["info"] = info };
        var trackers = magnet.AnnounceUrls ?? [];
        if (trackers.Count > 0)
        {
            dict["announce"] = new BEncodedString(trackers[0]);
            var tiers = new BEncodedList();
            foreach (var t in trackers) tiers.Add(new BEncodedList { new BEncodedString(t) });
            dict["announce-list"] = tiers;
        }
        return dict.Encode();
    }
}

/// <summary>An error worth showing to the user as-is.</summary>
public sealed class UserError(string message) : Exception(message);

public static class Trackers
{
    /// <summary>Large, long-running public trackers. More trackers = more peers found sooner.</summary>
    public static readonly string[] Public =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.dler.org:6969/announce",
        "udp://opentracker.io:6969/announce",
        "udp://tracker.tiny-vps.com:6969/announce",
        "udp://tracker.theoks.net:6969/announce",
        "udp://tracker.qu.ax:6969/announce",
        "udp://tracker-udp.gbitt.info:80/announce",
        "udp://retracker01-msk-virt.corbina.net:80/announce",
        "https://tracker.tamersunion.org:443/announce",
        "http://tracker.opentrackr.org:1337/announce",
    ];

    public static IEnumerable<string> Parse(string text) =>
        text.Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "udp" or "http" or "https");
}
