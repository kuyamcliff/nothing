using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipFx.Platform;
using ClipFx.Platform.Simulated;
using ClipFx.Web;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace ClipFx.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    static readonly string ChromeId = SimulatedPlatform.Apps.First(a => a.Pid == 1001).Identity.Id;
    static readonly string UpdateId = SimulatedPlatform.Apps.First(a => a.Pid == 2001).Identity.Id;

    readonly string _dir = Directory.CreateTempSubdirectory("clipfx-test").FullName;
    WebApplication _app = null!;
    PlatformServices _platform = null!;
    HttpClient _http = null!;

    async Task StartAsync()
    {
        _platform = SimulatedPlatform.Create(autoTraffic: false);
        _app = ServerHost.Build(new ServerOptions { Port = 0, DataDir = _dir }, _platform);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = ServerHost.BoundAddress(_app) };
        _http.DefaultRequestHeaders.Add("X-ClipFx", "1");
    }

    async Task StopAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public Task InitializeAsync() => StartAsync();
    public Task DisposeAsync() => StopAsync();

    async Task<JsonElement> Snapshot() => await _http.GetFromJsonAsync<JsonElement>("/api/snapshot");

    async Task<JsonElement?> App(string id)
    {
        foreach (var a in (await Snapshot()).GetProperty("apps").EnumerateArray())
            if (a.GetProperty("id").GetString() == id) return a;
        return null;
    }

    async Task Inject(int pid, long bytes, bool sent = false) =>
        (await _http.PostAsJsonAsync("/api/dev/traffic", new { pid, bytes, sent })).EnsureSuccessStatusCode();

    async Task<JsonElement> WaitFor(Func<JsonElement, bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            var s = await Snapshot();
            if (cond(s)) return s;
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met: " + s);
            await Task.Delay(50);
        }
    }

    static bool AppIs(JsonElement s, string id, Func<JsonElement, bool> f) =>
        s.GetProperty("apps").EnumerateArray().Any(a => a.GetProperty("id").GetString() == id && f(a));

    [Fact]
    public async Task ServesUi()
    {
        var html = await _http.GetStringAsync("/");
        Assert.Contains("ClipFx Manager", html);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/app.css")).StatusCode);
    }

    [Fact]
    public async Task RecordsTraffic()
    {
        await Inject(1001, 5000);
        await Inject(1001, 700, sent: true);
        var app = (await App(ChromeId))!.Value;
        Assert.Equal(5700, app.GetProperty("total").GetInt64());
        Assert.Equal("Google Chrome", app.GetProperty("name").GetString());
    }

    [Fact]
    public async Task SwitchOffIsAbsolute()
    {
        await Inject(1001, 1000);
        (await _http.PostAsJsonAsync($"/api/apps/{ChromeId}/blocked", new { blocked = true })).EnsureSuccessStatusCode();
        await WaitFor(s => AppIs(s, ChromeId, a => a.GetProperty("blocked").GetBoolean() && a.GetProperty("applied").GetBoolean()));

        await Inject(1001, 50_000); // dropped by the firewall
        Assert.Equal(1000, (await App(ChromeId))!.Value.GetProperty("total").GetInt64());

        (await _http.PostAsJsonAsync($"/api/apps/{ChromeId}/blocked", new { blocked = false })).EnsureSuccessStatusCode();
        await WaitFor(s => AppIs(s, ChromeId, a => !a.GetProperty("effectiveBlocked").GetBoolean() && a.GetProperty("applied").GetBoolean()));
        await Inject(1001, 1);
        Assert.Equal(1001, (await App(ChromeId))!.Value.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task AppLimitCutsOffAtTheLimit()
    {
        await Inject(1001, 10);
        (await _http.PostAsJsonAsync($"/api/apps/{ChromeId}/limit", new { bytes = 1_000_000 })).EnsureSuccessStatusCode();
        await Inject(1001, 999_990);
        var s = await WaitFor(s => AppIs(s, ChromeId, a => a.GetProperty("limitReached").GetBoolean() && a.GetProperty("applied").GetBoolean()));
        await Inject(1001, 5_000_000);
        Assert.Equal(1_000_000, (await App(ChromeId))!.Value.GetProperty("total").GetInt64());
        Assert.True(((SimulatedFirewall)_platform.Firewall).GetApplied().ContainsKey(SimulatedPlatform.Apps[0].Identity.Key));
    }

    [Fact]
    public async Task OverallLimitBlocksAllApps()
    {
        (await _http.PostAsJsonAsync("/api/overall/limit", new { bytes = 2000 })).EnsureSuccessStatusCode();
        await Inject(1001, 1500);
        await Inject(2001, 600);
        await WaitFor(s => s.GetProperty("overall").GetProperty("limitReached").GetBoolean() && s.GetProperty("overall").GetProperty("applied").GetBoolean());
        await Inject(1002, 10_000);
        await Inject(4, 10_000);
        Assert.Equal(2100, (await Snapshot()).GetProperty("overall").GetProperty("total").GetInt64());

        (await _http.PostAsync("/api/usage/reset", null)).EnsureSuccessStatusCode();
        await WaitFor(s => !s.GetProperty("overall").GetProperty("limitReached").GetBoolean() && s.GetProperty("overall").GetProperty("applied").GetBoolean());
        await Inject(1002, 10);
        Assert.Equal(10, (await Snapshot()).GetProperty("overall").GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task EverythingSurvivesARestart()
    {
        await Inject(1001, 4321);
        await Inject(2001, 100);
        (await _http.PostAsJsonAsync($"/api/apps/{UpdateId}/blocked", new { blocked = true })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync($"/api/apps/{ChromeId}/limit", new { bytes = 9_000_000 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/settings", new { theme = "dark", showSystem = true })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/overall/limit", new { bytes = 5_000_000_000 })).EnsureSuccessStatusCode();

        await StopAsync(); // graceful shutdown saves state
        await StartAsync();

        var s = await Snapshot();
        Assert.Equal("dark", s.GetProperty("settings").GetProperty("theme").GetString());
        Assert.True(s.GetProperty("settings").GetProperty("showSystem").GetBoolean());
        Assert.Equal(5_000_000_000, s.GetProperty("overall").GetProperty("limit").GetInt64());
        Assert.Equal(4421, s.GetProperty("overall").GetProperty("total").GetInt64());
        var chrome = (await App(ChromeId))!.Value;
        Assert.Equal(4321, chrome.GetProperty("total").GetInt64());
        Assert.Equal(9_000_000, chrome.GetProperty("limit").GetInt64());
        // The new process re-applies the block on its own.
        await WaitFor(x => AppIs(x, UpdateId, a => a.GetProperty("blocked").GetBoolean() && a.GetProperty("applied").GetBoolean()));
        await Inject(2001, 777);
        Assert.Equal(100, (await App(UpdateId))!.Value.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task StateIsSavedWhileRunning()
    {
        await Inject(1001, 2468);
        var file = Path.Combine(_dir, "state.json");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!(File.Exists(file) && File.ReadAllText(file).Contains("2468")))
        {
            Assert.True(DateTime.UtcNow < until, "state was not saved in time");
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task ForgetAndReset()
    {
        await Inject(1001, 500);
        (await _http.PostAsync($"/api/apps/{ChromeId}/reset", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await App(ChromeId))!.Value.GetProperty("total").GetInt64());

        (await _http.PostAsJsonAsync($"/api/apps/{ChromeId}/blocked", new { blocked = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await _http.DeleteAsync($"/api/apps/{ChromeId}")).StatusCode);
        (await _http.PostAsJsonAsync($"/api/apps/{ChromeId}/blocked", new { blocked = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/apps/{ChromeId}")).StatusCode);
        Assert.Null(await App(ChromeId));
    }

    [Fact]
    public async Task RejectsBadInput()
    {
        await Inject(1001, 1);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsJsonAsync("/api/apps/doesnotexist/blocked", new { blocked = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync($"/api/apps/{ChromeId}/limit", new { bytes = -5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/settings", new { resetPeriod = "hourly" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/settings", new { theme = "neon" })).StatusCode);
    }

    [Fact]
    public async Task RejectsCrossSiteAndForeignHosts()
    {
        using var plain = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync("/api/usage/reset", new { })).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/snapshot");
        req.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task StreamsLiveSnapshots()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var res = await _http.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
        int events = 0;
        while (events < 2)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(line[6..]);
                Assert.True(doc.RootElement.TryGetProperty("overall", out _));
                events++;
            }
        }
        Assert.Equal(2, events);
    }
}

public sealed class UnattributedApiTests : IAsyncLifetime
{
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _app = ServerHost.Build(new ServerOptions { Port = 0, DataDir = Directory.CreateTempSubdirectory().FullName }, SimulatedPlatform.Create(autoTraffic: false));
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = ServerHost.BoundAddress(_app) };
        _http.DefaultRequestHeaders.Add("X-ClipFx", "1");
    }

    public async Task DisposeAsync() { _http.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); }

    [Fact]
    public async Task EveryByteOnTheWireShowsUp()
    {
        (await _http.PostAsJsonAsync("/api/dev/traffic", new { pid = 1001, bytes = 1_000_000, sent = false })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/dev/unattributed", new { bytes = 300_000, sent = false, vm = false })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/dev/unattributed", new { bytes = 200_000, sent = false, vm = true })).EnsureSuccessStatusCode();

        JsonElement snap = default;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            snap = await _http.GetFromJsonAsync<JsonElement>("/api/snapshot");
            if (snap.GetProperty("overall").GetProperty("total").GetInt64() >= 1_500_000) break;
            await Task.Delay(200);
        }
        var apps = snap.GetProperty("apps").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        Assert.Equal(1_500_000, snap.GetProperty("overall").GetProperty("total").GetInt64());
        Assert.Equal(300_000, apps["Other network traffic"].GetProperty("total").GetInt64());
        Assert.Equal(200_000, apps["Virtual machines and WSL"].GetProperty("total").GetInt64());
        Assert.False(apps["Other network traffic"].GetProperty("isSystem").GetBoolean());
        Assert.False(apps["Other network traffic"].GetProperty("canBlock").GetBoolean());
        Assert.Equal("Network", apps["Other network traffic"].GetProperty("kind").GetString());
    }
}
