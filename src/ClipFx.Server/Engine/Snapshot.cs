using ClipFx.Model;

namespace ClipFx.Engine;

public sealed record AppView(
    string Id, string Name, string? Path, AppKind Kind, bool IsSystem, bool CanBlock,
    long Sent, long Received, long Total, double Up, double Down,
    bool Blocked, long? Limit, bool LimitReached, bool EffectiveBlocked, bool Applied,
    bool Active, DateTime FirstSeen, DateTime LastSeen);

public sealed record RatePoint(double Up, double Down);

public sealed record OverallView(
    long Sent, long Received, long Total, double Up, double Down,
    long? Limit, bool LimitReached, bool Applied, IReadOnlyList<RatePoint> History);

public sealed record PeriodView(DateTime Start, DateTime? NextReset, string Mode, int ResetDay);

public sealed record StatusView(
    bool Monitoring, string? MonitorError,
    bool FirewallReady, string? FirewallError, bool FirewallEnabled, bool Simulated);

public sealed record SettingsView(string Theme, string ResetPeriod, int ResetDay, bool ShowSystem);

public sealed record Snapshot(
    long Version, DateTime Time, StatusView Status, SettingsView Settings,
    PeriodView Period, OverallView Overall, IReadOnlyList<AppView> Apps);
