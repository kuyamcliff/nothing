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

namespace ClipFx.Share.Web;

/// <summary>Native things the UI asks the desktop shell to do.</summary>
public interface IHostActions
{
    Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    Task<string?> PickFolderAsync(string? title = null) => Task.FromResult<string?>(null);
    void Open(string path) { }
    void Reveal(string path) { }
    void CopyText(string text) { }
    bool StartWithWindows { get => false; set { } }
}

public sealed record SendBody(string[] Targets, string[]? Paths, string? Text);
public sealed record PathsBody(string[] Paths);
public sealed record AddressBody(string Address);
public sealed record TrustBody(bool Trusted);
public sealed record AcceptBody(bool Trust);
public sealed record PathBody(string Path);
public sealed record TextBody(string Text);

/// <summary>Serves the UI and its API on 127.0.0.1 for the WebView2 window.</summary>
public static class UiServer
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static WebApplication Build(ShareNode node, IHostActions host, int port, string? uiDir = null)
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

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 400; return; }
            if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-ClipFx"] != "1") { ctx.Response.StatusCode = 403; return; }
            await next();
        });

        IFileProvider files = uiDir != null ? new PhysicalFileProvider(Path.GetFullPath(uiDir)) : new ManifestEmbeddedFileProvider(typeof(UiServer).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

        var api = app.MapGroup("/api");
        api.MapGet("/state", () => Results.Json(State(node, host), Json));
        api.MapGet("/stream", (HttpContext ctx) => Stream(ctx, node, host));

        api.MapPost("/send", (SendBody body) =>
        {
            var items = ShareNode.Collect(body.Paths ?? [], body.Text);
            if (items.Count == 0) return Results.BadRequest(new { error = "Nothing to send." });
            var ids = node.Send(body.Targets, items);
            return ids.Count == 0 ? Results.BadRequest(new { error = "Pick a device." }) : Results.Json(new { ids });
        });
        api.MapPost("/inspect", (PathsBody body) => Results.Json(body.Paths.Select(Inspect).Where(x => x != null), Json));

        api.MapPost("/transfers/{id}/pause", (string id) => node.Pause(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/transfers/{id}/resume", (string id) => node.Resume(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/transfers/{id}/cancel", (string id) => node.Cancel(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/transfers/{id}/remove", (string id) => node.Remove(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/transfers/{id}/accept", (string id, AcceptBody b) => node.Accept(id, b.Trust) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/transfers/{id}/decline", (string id) => node.Decline(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/history/clear", () => { node.ClearHistory(); return Results.NoContent(); });

        api.MapPost("/peers/add", async (AddressBody b) =>
        {
            var p = await node.AddManualAsync(b.Address);
            return p is null ? Results.BadRequest(new { error = "Not reachable." }) : Results.Json(p, Json);
        });
        api.MapPost("/peers/scan", async () => { node.Refresh(); await node.ScanAsync(); return Results.NoContent(); });
        api.MapPost("/peers/{id}/trust", (string id, TrustBody b) => { node.SetTrusted(id, b.Trusted); return Results.NoContent(); });
        api.MapDelete("/peers/{id}", (string id) => { node.ForgetPeer(id); return Results.NoContent(); });

        api.MapPost("/settings", (JsonObject body) =>
        {
            node.UpdateSettings(s =>
            {
                if (body["deviceName"] is JsonValue n) s.DeviceName = n.GetValue<string>();
                if (body["saveDir"] is JsonValue d) s.SaveDir = d.GetValue<string>();
                if (body["askBeforeReceive"] is JsonValue a) s.AskBeforeReceive = a.GetValue<bool>();
                if (body["streams"] is JsonValue st) s.Streams = st.GetValue<int>();
                if (body["verify"] is JsonValue v) s.Verify = v.GetValue<bool>();
                if (body["conflict"] is JsonValue c) s.Conflict = c.GetValue<string>();
                if (body["theme"] is JsonValue t && t.GetValue<string>() is "system" or "light" or "dark") s.Theme = t.GetValue<string>();
                if (body["closeToTray"] is JsonValue ct) s.CloseToTray = ct.GetValue<bool>();
                if (body["openFolderWhenDone"] is JsonValue of) s.OpenFolderWhenDone = of.GetValue<bool>();
                if (body["organizeByType"] is JsonValue o) s.OrganizeByType = o.GetValue<bool>();
                if (body["discoverable"] is JsonValue di) s.Discoverable = di.GetValue<bool>();
                if (body["startWithWindows"] is JsonValue sw) { s.StartWithWindows = sw.GetValue<bool>(); host.StartWithWindows = s.StartWithWindows; }
            });
            return Results.NoContent();
        });

        api.MapPost("/host/pick-files", async () => Results.Json(await host.PickFilesAsync()));
        api.MapPost("/host/pick-folder", async () => Results.Json(await host.PickFolderAsync()));
        api.MapPost("/host/pick-save-dir", async () =>
        {
            var dir = await host.PickFolderAsync("Save received files to");
            if (dir != null) node.UpdateSettings(s => s.SaveDir = dir);
            return Results.Json(dir);
        });
        api.MapPost("/host/open", (PathBody b) => { host.Open(b.Path); return Results.NoContent(); });
        api.MapPost("/host/reveal", (PathBody b) => { host.Reveal(b.Path); return Results.NoContent(); });
        api.MapPost("/host/copy", (TextBody b) => { host.CopyText(b.Text); return Results.NoContent(); });
        return app;
    }

    public static object? Inspect(string path)
    {
        try
        {
            if (File.Exists(path)) { var f = new FileInfo(path); return new { path = f.FullName, name = f.Name, size = f.Length, isDir = false, files = 1 }; }
            if (Directory.Exists(path))
            {
                var d = new DirectoryInfo(path);
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                long size = 0; int count = 0;
                foreach (var f in d.EnumerateFiles("*", opts)) { size += f.Length; count++; }
                return new { path = d.FullName, name = d.Name, size, isDir = true, files = count };
            }
        }
        catch { }
        return null;
    }

    static int _uiClients;

    public static object State(ShareNode node, IHostActions host)
    {
        var s = node.Settings;
        return new
        {
            version = node.Version,
            uiClients = Volatile.Read(ref _uiClients),
            self = node.SelfInfo(),
            settings = new
            {
                s.DeviceName, s.SaveDir, s.AskBeforeReceive, s.Streams, s.Verify, s.Conflict, s.Theme, s.CloseToTray,
                s.OpenFolderWhenDone, s.OrganizeByType, s.Discoverable, startWithWindows = host.StartWithWindows, trusted = s.TrustedDevices,
            },
            peers = node.Peers(),
            transfers = node.Transfers(),
        };
    }

    static async Task Stream(HttpContext ctx, ShareNode node, IHostActions host)
    {
        var ct = ctx.RequestAborted;
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var changed = new SemaphoreSlim(0);
        void OnChanged() { if (changed.CurrentCount == 0) changed.Release(); }
        node.Changed += OnChanged;
        Interlocked.Increment(ref _uiClients);
        try
        {
            await ctx.Response.WriteAsync("retry: 1000\n\n", ct);
            while (!ct.IsCancellationRequested)
            {
                var json = JsonSerializer.Serialize(State(node, host), Json);
                await ctx.Response.WriteAsync("data: " + json + "\n\n", Encoding.UTF8, ct);
                await ctx.Response.Body.FlushAsync(ct);
                await changed.WaitAsync(TimeSpan.FromMilliseconds(500), ct);
                await Task.Delay(120, ct); // coalesce bursts
            }
        }
        catch (OperationCanceledException) { }
        finally { node.Changed -= OnChanged; Interlocked.Decrement(ref _uiClients); }
    }

    public static Uri Address(WebApplication app) =>
        new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
}
