using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ClipFx.Torrents.Web;

/// <summary>Native things the UI asks the desktop shell to do.</summary>
public interface IHostActions
{
    Task<IReadOnlyList<string>> PickTorrentsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    Task<string?> PickFolderAsync(string? title = null) => Task.FromResult<string?>(null);
    void Open(string path) { }
    void Reveal(string path) { }
    void CopyText(string text) { }
    string? ReadClipboard() => null;
    bool StartWithWindows { get => false; set { } }
    bool IsDefaultApp { get => false; }
    void MakeDefaultApp() { }
}

public sealed record PreviewBody(string Source, bool Fetch);
public sealed record ManyBody(string[] Sources, bool? Paused);
public sealed record RemoveBody(bool DeleteFiles);
public sealed record OnBody(bool On);
public sealed record PriorityBody(int[] Files, string Priority);
public sealed record OrderBody(int Delta);
public sealed record DirBody(string Dir);
public sealed record PeersBody(string[] Addresses);
public sealed record ActionBody(string Action);
public sealed record PathBody(string Path);
public sealed record TextBody(string Text);
public sealed record TitleBody(string? Title);

/// <summary>Serves the UI and its API on 127.0.0.1 for the WebView2 window.</summary>
public static class UiServer
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static WebApplication Build(TorrentClient client, IHostActions host, int port, string? uiDir = null)
    {
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        var app = b.Build();

        // Only our own window may talk to this: other sites can't set custom headers cross-origin.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 400; return; }
            if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-ClipFx"] != "1") { ctx.Response.StatusCode = 403; return; }
            try { await next(); }
            catch (UserError e) when (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = e.Message });
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        });

        IFileProvider files = uiDir != null ? new PhysicalFileProvider(Path.GetFullPath(uiDir)) : new ManifestEmbeddedFileProvider(typeof(UiServer).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

        var api = app.MapGroup("/api");
        api.MapGet("/state", () => Results.Json(State(client, host), Json));
        api.MapGet("/stream", (HttpContext ctx) => Stream(ctx, client, host));
        api.MapGet("/torrents/{id}", async (string id) => await client.DetailAsync(id) is { } d ? Results.Json(d, Json) : Results.NotFound());

        api.MapPost("/preview", async (PreviewBody body, HttpContext ctx) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            return Results.Json(await client.PreviewAsync(body.Source, body.Fetch, cts.Token), Json);
        });
        api.MapPost("/add", async (AddRequest body) => Results.Json(await client.AddAsync(body), Json));
        api.MapPost("/add-many", async (ManyBody body) =>
        {
            var added = new List<AddResult>();
            var errors = new List<string>();
            foreach (var s in body.Sources.SelectMany(Sources.FindLinks).Distinct())
            {
                try { added.Add(await client.AddAsync(new AddRequest(s, null, null, body.Paused, null, null))); }
                catch (UserError e) { errors.Add(e.Message); }
            }
            if (added.Count == 0 && errors.Count > 0) throw new UserError(errors[0]);
            return Results.Json(new { added, errors }, Json);
        });

        api.MapPost("/torrents/{id}/pause", async (string id) => await client.PauseAsync(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/torrents/{id}/resume", async (string id) => await client.ResumeAsync(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/torrents/{id}/force", async (string id) => await client.ResumeAsync(id, force: true) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/torrents/{id}/recheck", (string id) => { _ = client.RecheckAsync(id); return Results.NoContent(); });
        api.MapPost("/torrents/{id}/remove", async (string id, RemoveBody b) => await client.RemoveAsync(id, b.DeleteFiles) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/torrents/{id}/sequential", async (string id, OnBody b) => await client.SetSequentialAsync(id, b.On) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/torrents/{id}/priority", async (string id, PriorityBody b) => await client.SetPriorityAsync(id, b.Files, b.Priority) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/torrents/{id}/order", async (string id, OrderBody b) => await client.MoveOrderAsync(id, b.Delta) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/torrents/{id}/peers", async (string id, PeersBody b) =>
        {
            int n = await client.AddPeersAsync(id, b.Addresses);
            return n == 0 ? Results.BadRequest(new { error = "Use ip:port, one per line." }) : Results.Json(new { added = n });
        });
        api.MapPost("/torrents/{id}/move", async (string id) =>
        {
            var dir = await host.PickFolderAsync("Move files to");
            if (dir == null) return Results.NoContent();
            _ = client.MoveAsync(id, dir);
            return Results.Json(dir);
        });
        api.MapPost("/torrents/{id}/copy-magnet", (string id) =>
        {
            var m = client.MagnetOf(id);
            if (m == null) return Results.NotFound();
            host.CopyText(m);
            return Results.NoContent();
        });
        api.MapPost("/pause-all", async () => { await client.PauseAllAsync(); return Results.NoContent(); });
        api.MapPost("/resume-all", async () => { await client.ResumeAllAsync(); return Results.NoContent(); });
        api.MapPost("/when-done", (ActionBody b) =>
        {
            client.WhenDone = b.Action is "sleep" or "shutdown" or "hibernate" ? b.Action : "nothing";
            return Results.NoContent();
        });
        api.MapPost("/free", (DirBody b) =>
        {
            try { return Results.Json(new { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(b.Dir))!).AvailableFreeSpace }); }
            catch { return Results.Json(new { free = -1L }); }
        });

        api.MapPost("/settings", async (JsonObject body) =>
        {
            bool? startup = null;
            await client.UpdateSettingsAsync(s =>
            {
                string? Str(string k) => body[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : null;
                bool? Bool(string k) => body[k] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : null;
                double? Num(string k) => body[k] is JsonValue v && v.TryGetValue<double>(out var x) ? x : null;

                if (Str("saveDir") is { Length: > 0 } sd) s.SaveDir = sd;
                if (body.ContainsKey("moveDoneTo")) s.MoveDoneTo = Str("moveDoneTo") is { Length: > 0 } md ? md : null;
                if (Bool("showAddDialog") is { } sad) s.ShowAddDialog = sad;
                if (Bool("sequentialByDefault") is { } sq) s.SequentialByDefault = sq;
                if (Num("activeDownloads") is { } ad) s.ActiveDownloads = (int)ad;
                if (Num("downloadLimitKB") is { } dl) s.DownloadLimitKB = (int)Math.Max(0, dl);
                if (Num("uploadLimitKB") is { } ul) s.UploadLimitKB = (int)Math.Max(0, ul);
                if (Num("connectionsPerTorrent") is { } cp) s.ConnectionsPerTorrent = (int)cp;
                if (Num("connectionsTotal") is { } ctot) s.ConnectionsTotal = (int)ctot;
                if (Num("uploadSlots") is { } us) s.UploadSlots = (int)us;
                if (Num("port") is { } p) s.Port = (int)p;
                if (Bool("randomPort") == true) s.Port = Random.Shared.Next(20000, 60000);
                if (Bool("portForwarding") is { } pf) s.PortForwarding = pf;
                if (Bool("dht") is { } dht) s.Dht = dht;
                if (Bool("peerExchange") is { } pex) s.PeerExchange = pex;
                if (Bool("localPeers") is { } lp) s.LocalPeers = lp;
                if (Bool("extraTrackers") is { } et) s.ExtraTrackers = et;
                if (Str("customTrackers") is { } ctr) s.CustomTrackers = ctr;
                if (Str("encryption") is "prefer" or "require" or "off") s.Encryption = Str("encryption")!;
                if (Str("seeding") is "forever" or "ratio" or "stop") s.Seeding = Str("seeding")!;
                if (Num("seedRatio") is { } sr) s.SeedRatio = sr;
                if (Bool("keepAwake") is { } ka) s.KeepAwake = ka;
                if (Bool("notify") is { } n) s.Notify = n;
                if (Bool("openFolderWhenDone") is { } of) s.OpenFolderWhenDone = of;
                if (Bool("closeToTray") is { } ct) s.CloseToTray = ct;
                if (Str("theme") is "system" or "light" or "dark") s.Theme = Str("theme")!;
                if (Bool("startWithWindows") is { } sw) { s.StartWithWindows = sw; startup = sw; }
            });
            if (startup is { } on) host.StartWithWindows = on;
            return Results.NoContent();
        });

        api.MapPost("/host/pick-torrents", async () => Results.Json(await host.PickTorrentsAsync()));
        api.MapPost("/host/pick-folder", async (TitleBody b) => Results.Json(await host.PickFolderAsync(b.Title)));
        api.MapPost("/host/open", (PathBody b) => { host.Open(b.Path); return Results.NoContent(); });
        api.MapPost("/host/reveal", (PathBody b) => { host.Reveal(b.Path); return Results.NoContent(); });
        api.MapPost("/host/copy", (TextBody b) => { host.CopyText(b.Text); return Results.NoContent(); });
        api.MapPost("/host/clipboard", () => Results.Json(new { text = host.ReadClipboard() }));
        api.MapPost("/host/default-app", () => { host.MakeDefaultApp(); return Results.NoContent(); });
        return app;
    }

    static int _uiClients;

    public static object State(TorrentClient c, IHostActions host)
    {
        var s = c.Settings;
        return new
        {
            version = c.Version,
            uiClients = Volatile.Read(ref _uiClients),
            whenDone = c.WhenDone,
            isDefault = host.IsDefaultApp,
            settings = new
            {
                s.SaveDir, s.MoveDoneTo, s.ShowAddDialog, s.SequentialByDefault, s.ActiveDownloads,
                s.DownloadLimitKB, s.UploadLimitKB, s.ConnectionsPerTorrent, s.ConnectionsTotal, s.UploadSlots,
                s.Port, s.PortForwarding, s.Dht, s.PeerExchange, s.LocalPeers, s.ExtraTrackers, s.CustomTrackers, s.Encryption,
                s.Seeding, s.SeedRatio, s.KeepAwake, s.Notify, s.OpenFolderWhenDone, s.CloseToTray, s.Theme,
                startWithWindows = host.StartWithWindows,
            },
            global = c.Global(),
            torrents = c.Torrents(),
        };
    }

    static async Task Stream(HttpContext ctx, TorrentClient c, IHostActions host)
    {
        var ct = ctx.RequestAborted;
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var changed = new SemaphoreSlim(0);
        void OnChanged() { if (changed.CurrentCount == 0) changed.Release(); }
        c.Changed += OnChanged;
        Interlocked.Increment(ref _uiClients);
        try
        {
            await ctx.Response.WriteAsync("retry: 1000\n\n", ct);
            while (!ct.IsCancellationRequested)
            {
                var json = JsonSerializer.Serialize(State(c, host), Json);
                await ctx.Response.WriteAsync("data: " + json + "\n\n", Encoding.UTF8, ct);
                await ctx.Response.Body.FlushAsync(ct);
                await changed.WaitAsync(TimeSpan.FromMilliseconds(1000), ct);
                await Task.Delay(150, ct); // coalesce bursts
            }
        }
        catch (OperationCanceledException) { }
        finally { c.Changed -= OnChanged; Interlocked.Decrement(ref _uiClients); }
    }

    public static Uri Address(WebApplication app) =>
        new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
}
