using ClipFx.Engine;
using ClipFx.Model;
using ClipFx.Platform;
using ClipFx.Platform.Simulated;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClipFx.Tests;

public class EnforcerTests
{
    static readonly AppIdentity Chrome = SimulatedPlatform.Apps.First(a => a.Pid == 1001).Identity;
    static readonly AppIdentity Update = SimulatedPlatform.Apps.First(a => a.Pid == 2001).Identity;

    sealed class CountingKiller : IConnectionKiller
    {
        public List<int> Killed { get; } = [];
        public int KillAllCalls;
        public void Kill(IReadOnlyCollection<int> pids) => Killed.AddRange(pids);
        public void KillAll() => KillAllCalls++;
    }

    sealed class BrokenFirewall : IFirewall
    {
        public IReadOnlyDictionary<string, string> GetApplied() => new Dictionary<string, string>();
        public string Signature(AppIdentity identity) => "x";
        public void Block(AppIdentity identity) => throw new InvalidOperationException("Access is denied.");
        public void Unblock(string key) { }
        public void SetGlobalBlock(bool on) { }
        public bool IsEnabled() => true;
        public void Enable() { }
        public void RemoveAll() { }
    }

    static (UsageEngine, SimulatedFirewall, CountingKiller, Enforcer) Setup()
    {
        var engine = new UsageEngine(new PersistedState());
        var fw = new SimulatedFirewall();
        var killer = new CountingKiller();
        var p = new PlatformServices(SimulatedPlatform.Create().Traffic, new SimulatedResolver(), fw, killer, null!, true);
        var enf = new Enforcer(engine, p, new SnapshotHub(), NullLogger<Enforcer>.Instance);
        return (engine, fw, killer, enf);
    }

    [Fact]
    public void BlocksAndClosesConnections()
    {
        var (engine, fw, killer, enf) = Setup();
        engine.Record(Chrome, 1001, 10, false);
        engine.SetBlocked(Chrome.Id, true);
        enf.Reconcile();
        Assert.True(fw.GetApplied().ContainsKey(Chrome.Key));
        Assert.True(enf.IsApplied(Chrome.Key));
        Assert.Contains(1001, killer.Killed);
        Assert.False(fw.Allows(Chrome));

        engine.SetBlocked(Chrome.Id, false);
        enf.Reconcile();
        Assert.False(fw.GetApplied().ContainsKey(Chrome.Key));
        Assert.True(fw.Allows(Chrome));
    }

    [Fact]
    public void RepairsRulesRemovedFromOutside()
    {
        var (engine, fw, _, enf) = Setup();
        engine.Record(Update, 2001, 10, false);
        engine.SetBlocked(Update.Id, true);
        enf.Reconcile();
        fw.Unblock(Update.Key); // e.g. someone deleted the rule in wf.msc
        Assert.True(fw.Allows(Update));
        enf.Reconcile();
        Assert.False(fw.Allows(Update));
    }

    [Fact]
    public void RemovesRulesThatAreNoLongerWanted()
    {
        var (_, fw, _, enf) = Setup();
        fw.Block(Chrome); // left over from a previous run, app unknown to engine now
        enf.Reconcile();
        Assert.Empty(fw.GetApplied());
    }

    [Fact]
    public void DoesNotRewriteRulesThatAreAlreadyCorrect()
    {
        var (engine, fw, _, enf) = Setup();
        engine.Record(Chrome, 1001, 10, false);
        engine.SetBlocked(Chrome.Id, true);
        enf.Reconcile();
        int writes = fw.Writes;
        enf.Reconcile();
        enf.Reconcile();
        Assert.Equal(writes, fw.Writes);
    }

    [Fact]
    public void OverallLimitTurnsOnGlobalBlock()
    {
        var (engine, fw, killer, enf) = Setup();
        engine.SetOverallLimit(100);
        engine.Record(Chrome, 1001, 150, false);
        enf.Reconcile();
        Assert.True(enf.GlobalApplied);
        Assert.Equal(1, killer.KillAllCalls);
        Assert.False(fw.Allows(Update));
        engine.SetOverallLimit(10_000);
        enf.Reconcile();
        Assert.False(enf.GlobalApplied);
        Assert.True(fw.Allows(Update));
    }

    [Fact]
    public void ReportsFirewallErrors()
    {
        var engine = new UsageEngine(new PersistedState());
        var p = new PlatformServices(SimulatedPlatform.Create().Traffic, new SimulatedResolver(), new BrokenFirewall(), new CountingKiller(), null!, true);
        var enf = new Enforcer(engine, p, new SnapshotHub(), NullLogger<Enforcer>.Instance);
        engine.Record(Chrome, 1001, 1, false);
        engine.SetBlocked(Chrome.Id, true);
        enf.Reconcile();
        Assert.Equal("Access is denied.", enf.LastError);
        Assert.False(enf.IsApplied(Chrome.Key));
    }
}
