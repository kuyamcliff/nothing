using System.Text.Json.Serialization;

namespace ClipFx.Model;

public sealed class Settings
{
    public string Theme { get; set; } = "system";          // system | light | dark
    public string ResetPeriod { get; set; } = "monthly";   // daily | weekly | monthly | never
    public int ResetDay { get; set; } = 1;                  // day of month for monthly
    public bool ShowSystem { get; set; }
    public long? OverallLimit { get; set; }

    public Settings Clone() => (Settings)MemberwiseClone();
}

public sealed class AppRecord
{
    public required AppIdentity Identity { get; set; }
    public long Sent { get; set; }
    public long Received { get; set; }
    public bool Blocked { get; set; }
    public long? Limit { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    [JsonIgnore] public long Total => Sent + Received;
    [JsonIgnore] public bool LimitReached => Limit is long l && Total >= l;
    [JsonIgnore] public bool EffectiveBlocked => Blocked || LimitReached;
}

public sealed class PersistedState
{
    public int Version { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public DateTime PeriodStart { get; set; }
    public long OverallSent { get; set; }
    public long OverallReceived { get; set; }
    public Dictionary<string, AppRecord> Apps { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore] public long OverallTotal => OverallSent + OverallReceived;
    [JsonIgnore] public bool OverallLimitReached => Settings.OverallLimit is long l && OverallTotal >= l;
}
