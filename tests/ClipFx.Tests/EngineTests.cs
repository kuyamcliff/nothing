using ClipFx.Engine;
using ClipFx.Model;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ClipFx.Tests;

public class EngineTests
{
    static readonly AppIdentity Chrome = new(AppIdentity.ProgramKey(@"C:\chrome.exe"), AppKind.Program, "Chrome", @"C:\chrome.exe", [], null, null, false);
    static readonly AppIdentity Steam = new(AppIdentity.ProgramKey(@"C:\steam.exe"), AppKind.Program, "Steam", @"C:\steam.exe", [], null, null, false);

    sealed class NoEnforcement : IEnforcementState
    {
        public bool IsApplied(string key) => false;
        public bool GlobalApplied => false;
    }

    static StatusView Status => new(true, null, true, null, true, true);

    static (UsageEngine, FakeTimeProvider) New(PersistedState? s = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return (new UsageEngine(s ?? new PersistedState(), time), time);
    }

    static AppView View(UsageEngine e, AppIdentity id) => e.GetSnapshot(new NoEnforcement(), Status).Apps.Single(a => a.Id == id.Id);

    [Fact]
    public void CountsPerAppAndOverall()
    {
        var (e, _) = New();
        e.Record(Chrome, 10, 1000, sent: false);
        e.Record(Chrome, 10, 200, sent: true);
        e.Record(Steam, 11, 50, sent: false);
        var snap = e.GetSnapshot(new NoEnforcement(), Status);
        Assert.Equal(1250, snap.Overall.Total);
        Assert.Equal(1000, View(e, Chrome).Received);
        Assert.Equal(200, View(e, Chrome).Sent);
        Assert.Equal(50, View(e, Steam).Total);
    }

    [Fact]
    public void TickComputesRates()
    {
        var (e, time) = New();
        e.Record(Chrome, 10, 4000, sent: false);
        time.Advance(TimeSpan.FromSeconds(2));
        e.Tick();
        Assert.Equal(2000, View(e, Chrome).Down, 3);
        Assert.Equal(2000, e.GetSnapshot(new NoEnforcement(), Status).Overall.Down, 3);
        time.Advance(TimeSpan.FromSeconds(1));
        e.Tick();
        Assert.Equal(0, View(e, Chrome).Down);
    }

    [Fact]
    public void AppLimitBlocksTheMomentItIsReached()
    {
        var (e, _) = New();
        e.Record(Chrome, 10, 100, false);
        e.SetLimit(Chrome.Id, 1000);
        int signals = 0;
        e.PolicyChanged += () => signals++;

        e.Record(Chrome, 10, 899, false);
        Assert.Empty(e.Desired().Blocked);
        Assert.Equal(0, signals);

        e.Record(Chrome, 10, 1, false); // exactly at the limit
        Assert.Equal(1, signals);
        Assert.Contains(e.Desired().Blocked, i => i.Key == Chrome.Key);
        Assert.True(View(e, Chrome).LimitReached);

        e.Record(Chrome, 10, 500, false);
        Assert.Equal(1, signals); // crossing is signalled once

        e.SetLimit(Chrome.Id, 10_000); // raising the limit lets it back on
        Assert.Empty(e.Desired().Blocked);
    }

    [Fact]
    public void ManualBlockAndUnblock()
    {
        var (e, _) = New();
        e.Record(Steam, 11, 10, false);
        Assert.True(e.SetBlocked(Steam.Id, true));
        Assert.Contains(e.Desired().Blocked, i => i.Key == Steam.Key);
        Assert.True(e.SetBlocked(Steam.Id, false));
        Assert.Empty(e.Desired().Blocked);
        Assert.False(e.SetBlocked("nope", true));
    }

    [Fact]
    public void OverallLimitBlocksEverything()
    {
        var (e, _) = New();
        e.SetOverallLimit(1000);
        e.Record(Chrome, 10, 600, false);
        Assert.False(e.Desired().Global);
        e.Record(Steam, 11, 400, true);
        Assert.True(e.Desired().Global);
        Assert.True(e.GetSnapshot(new NoEnforcement(), Status).Overall.LimitReached);
        e.SetOverallLimit(null);
        Assert.False(e.Desired().Global);
    }

    [Fact]
    public void ResetAppKeepsOverallTotal()
    {
        var (e, _) = New();
        e.Record(Chrome, 10, 600, false);
        e.SetLimit(Chrome.Id, 500);
        Assert.NotEmpty(e.Desired().Blocked);
        e.ResetApp(Chrome.Id);
        Assert.Equal(0, View(e, Chrome).Total);
        Assert.Equal(600, e.GetSnapshot(new NoEnforcement(), Status).Overall.Total);
        Assert.Empty(e.Desired().Blocked);
    }

    [Fact]
    public void ResetUsageClearsEverythingButKeepsPolicy()
    {
        var (e, _) = New();
        e.Record(Chrome, 10, 600, false);
        e.SetBlocked(Chrome.Id, true);
        e.SetLimit(Chrome.Id, 5000);
        e.ResetUsage();
        var v = View(e, Chrome);
        Assert.Equal(0, v.Total);
        Assert.True(v.Blocked);
        Assert.Equal(5000, v.Limit);
    }

    [Fact]
    public void PeriodRolloverResetsCountersAndLiftsLimitBlocks()
    {
        var (e, time) = New();
        e.UpdateSettings(null, "daily", null, null);
        e.Record(Chrome, 10, 600, false);
        e.SetLimit(Chrome.Id, 500);
        e.Record(Steam, 11, 10, false);
        e.SetBlocked(Steam.Id, true);
        Assert.Equal(2, e.Desired().Blocked.Count);

        time.Advance(TimeSpan.FromHours(13)); // past midnight
        e.Tick();
        Assert.Equal(0, View(e, Chrome).Total);
        Assert.Equal(0, e.GetSnapshot(new NoEnforcement(), Status).Overall.Total);
        var blocked = e.Desired().Blocked.Select(i => i.Key).ToList();
        Assert.Equal([Steam.Key], blocked); // manual block survives, limit block lifted
    }

    [Fact]
    public void ChangingScheduleDoesNotWipeUsage()
    {
        var (e, _) = New();
        e.UpdateSettings(null, "never", null, null);
        e.Record(Chrome, 10, 600, false);
        e.UpdateSettings(null, "daily", null, null);
        e.Tick();
        Assert.Equal(600, View(e, Chrome).Total);
    }

    [Fact]
    public void ForgetRefusesBlockedApps()
    {
        var (e, _) = New();
        e.Record(Chrome, 10, 1, false);
        e.SetBlocked(Chrome.Id, true);
        Assert.Equal(UsageEngine.ForgetResult.Blocked, e.Forget(Chrome.Id));
        e.SetBlocked(Chrome.Id, false);
        Assert.Equal(UsageEngine.ForgetResult.Ok, e.Forget(Chrome.Id));
        Assert.Empty(e.GetSnapshot(new NoEnforcement(), Status).Apps);
    }

    [Fact]
    public void UnblockableIdentitiesAreNeverSentToTheFirewall()
    {
        var (e, _) = New();
        e.Record(AppIdentity.Unknown, 99, 10, false);
        e.SetBlocked(AppIdentity.Unknown.Id, true);
        Assert.Empty(e.Desired().Blocked);
    }

    [Fact]
    public void StateSurvivesSerialization()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new StateStore(dir);
        var (e, _) = New(store.Load());
        e.Record(Chrome, 10, 1234, false);
        e.SetBlocked(Chrome.Id, true);
        e.SetLimit(Chrome.Id, 9999);
        e.SetOverallLimit(5_000_000);
        e.UpdateSettings("dark", "weekly", null, true);
        store.Save(e.Serialize());
        Assert.False(e.IsDirty);

        var (e2, _) = New(new StateStore(dir).Load());
        var v = View(e2, Chrome);
        Assert.Equal(1234, v.Total);
        Assert.True(v.Blocked);
        Assert.Equal(9999, v.Limit);
        var snap = e2.GetSnapshot(new NoEnforcement(), Status);
        Assert.Equal(5_000_000, snap.Overall.Limit);
        Assert.Equal("dark", snap.Settings.Theme);
        Assert.Equal("weekly", snap.Settings.ResetPeriod);
        Assert.True(snap.Settings.ShowSystem);
        Assert.Contains(e2.Desired().Blocked, i => i.Key == Chrome.Key);
    }

    [Fact]
    public void CorruptStateFallsBackToBackup()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new StateStore(dir);
        var (e, _) = New(store.Load());
        e.Record(Chrome, 10, 100, false);
        store.Save(e.Serialize());
        e.Record(Chrome, 10, 100, false);
        store.Save(e.Serialize()); // previous file becomes the backup
        File.WriteAllText(Path.Combine(dir, "state.json"), "{ not json");
        var loaded = new StateStore(dir).Load();
        Assert.Equal(100, loaded.Apps[Chrome.Key].Received);
        Assert.True(File.Exists(Path.Combine(dir, "state.json.corrupt")));
    }

    [Fact]
    public void MissingStateStartsFresh()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Assert.Empty(new StateStore(dir).Load().Apps);
    }
}
