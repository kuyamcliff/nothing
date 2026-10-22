using ClipFx.Engine;
using ClipFx.Platform;

namespace ClipFx.Web;

public sealed class SnapshotBuilder(UsageEngine engine, Enforcer enforcer, TrafficMonitor monitor, PlatformServices platform)
{
    public Snapshot Build() => engine.GetSnapshot(enforcer, new StatusView(
        monitor.IsRunning, monitor.Error,
        enforcer.LastError == null, enforcer.LastError, enforcer.FirewallEnabled, platform.Simulated));
}
