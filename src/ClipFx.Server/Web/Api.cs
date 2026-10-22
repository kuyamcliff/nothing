using System.Text;
using System.Text.Json;
using ClipFx.Engine;
using ClipFx.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace ClipFx.Web;

public sealed record BlockedBody(bool Blocked);
public sealed record LimitBody(long? Bytes);
public sealed record SettingsBody(string? Theme, string? ResetPeriod, int? ResetDay, bool? ShowSystem);
public sealed record InjectBody(int Pid, long Bytes, bool Sent);
public sealed record AutoBody(bool On);

public static class Api
{
    const long MaxLimit = 1L << 50; // 1 PB

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/snapshot", (SnapshotBuilder s) => Results.Json(s.Build(), ServerHost.Json));

        api.MapGet("/stream", Stream);

        api.MapGet("/diag", () => Results.Json(Diag.Snapshot(), ServerHost.Json));

        api.MapPost("/apps/{id}/blocked", (string id, BlockedBody body, UsageEngine e, Enforcer enf) =>
        {
            var identity = e.IdentityOf(id);
            if (identity is null) return Results.NotFound();
            if (body.Blocked && !identity.CanBlock) return Results.Conflict(new { error = "This item can't be blocked." });
            e.SetBlocked(id, body.Blocked);
            return Results.NoContent();
        });

        api.MapPost("/apps/{id}/limit", (string id, LimitBody body, UsageEngine e) =>
        {
            if (body.Bytes is < 0 or > MaxLimit) return Results.BadRequest(new { error = "Invalid limit." });
            return e.SetLimit(id, body.Bytes) ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/apps/{id}/reset", (string id, UsageEngine e) =>
            e.ResetApp(id) ? Results.NoContent() : Results.NotFound());

        api.MapDelete("/apps/{id}", (string id, UsageEngine e) => e.Forget(id) switch
        {
            UsageEngine.ForgetResult.Ok => Results.NoContent(),
            UsageEngine.ForgetResult.Blocked => Results.Conflict(new { error = "Allow it before removing it." }),
            _ => Results.NotFound(),
        });

        api.MapGet("/apps/{id}/icon", (string id, UsageEngine e, PlatformServices p) =>
        {
            var identity = e.IdentityOf(id);
            var png = identity is null ? null : p.Icons.GetPng(identity);
            if (png is null) return Results.NotFound();
            return Results.File(png, "image/png");
        });

        api.MapPost("/overall/limit", (LimitBody body, UsageEngine e) =>
        {
            if (body.Bytes is < 0 or > MaxLimit) return Results.BadRequest(new { error = "Invalid limit." });
            e.SetOverallLimit(body.Bytes);
            return Results.NoContent();
        });

        api.MapPost("/usage/reset", (UsageEngine e) => { e.ResetUsage(); return Results.NoContent(); });

        api.MapPost("/settings", (SettingsBody body, UsageEngine e) =>
        {
            if (body.ResetPeriod != null && !Period.IsValid(body.ResetPeriod)) return Results.BadRequest(new { error = "Invalid period." });
            if (body.Theme is not (null or "system" or "light" or "dark")) return Results.BadRequest(new { error = "Invalid theme." });
            e.UpdateSettings(body.Theme, body.ResetPeriod, body.ResetDay, body.ShowSystem);
            return Results.NoContent();
        });

        api.MapPost("/firewall/enable", (Enforcer enf) =>
        {
            try { enf.EnableFirewall(); return Results.NoContent(); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // Test hooks, only present in simulation mode.
        api.MapPost("/dev/traffic", (InjectBody b, PlatformServices p) =>
        {
            if (p.Traffic is not ClipFx.Platform.Simulated.SimulatedTrafficSource sim) return Results.NotFound();
            sim.Inject(b.Pid, b.Bytes, b.Sent);
            return Results.NoContent();
        });
        api.MapPost("/dev/auto", (AutoBody b, PlatformServices p) =>
        {
            if (p.Traffic is not ClipFx.Platform.Simulated.SimulatedTrafficSource sim) return Results.NotFound();
            sim.AutoTraffic = b.On;
            return Results.NoContent();
        });
    }

    static async Task Stream(HttpContext ctx, SnapshotBuilder builder, SnapshotHub hub)
    {
        var ct = ctx.RequestAborted;
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            await ctx.Response.WriteAsync("retry: 1000\n\n", ct);
            while (!ct.IsCancellationRequested)
            {
                var json = JsonSerializer.Serialize(builder.Build(), ServerHost.Json);
                await ctx.Response.WriteAsync("data: " + json + "\n\n", Encoding.UTF8, ct);
                await ctx.Response.Body.FlushAsync(ct);
                await hub.WaitAsync(TimeSpan.FromSeconds(1), ct);
                await Task.Delay(100, ct); // coalesce bursts
            }
        }
        catch (OperationCanceledException) { }
    }
}
