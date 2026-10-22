using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipFx.Engine;
using ClipFx.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ClipFx.Web;

public static class ServerHost
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static WebApplication Build(ServerOptions options, PlatformServices platform, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        var store = new StateStore(options.DataDir);
        var engine = new UsageEngine(store.Load());
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(platform);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(engine);
        builder.Services.AddSingleton<SnapshotHub>();
        builder.Services.AddSingleton<Enforcer>();
        builder.Services.AddSingleton<TrafficMonitor>();
        builder.Services.AddSingleton<SnapshotBuilder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TrafficMonitor>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Enforcer>());
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        configure?.Invoke(builder);

        var app = builder.Build();
        app.Use(Guard);

        IFileProvider files = options.UiDir != null
            ? new PhysicalFileProvider(Path.GetFullPath(options.UiDir))
            : new ManifestEmbeddedFileProvider(typeof(ServerHost).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
        });

        Api.Map(app);
        return app;
    }

    /// <summary>Rejects DNS-rebinding hosts and cross-site writes.</summary>
    static async Task Guard(HttpContext ctx, Func<Task> next)
    {
        var host = ctx.Request.Host.Host;
        if (host is not ("127.0.0.1" or "localhost"))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
            && ctx.Request.Headers["X-ClipFx"] != "1")
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next();
    }

    public static Uri BoundAddress(WebApplication app)
    {
        var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(addr.Replace("[::]", "127.0.0.1"));
    }
}
