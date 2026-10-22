using ClipFx.Model;

namespace ClipFx.Engine;

public static class Period
{
    /// <summary>Start of the period that contains <paramref name="now"/> (local time), or null for "never".</summary>
    public static DateTime? CurrentStart(DateTime now, Settings s)
    {
        switch (s.ResetPeriod)
        {
            case "daily":
                return now.Date;
            case "weekly":
            {
                int diff = ((int)now.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                return now.Date.AddDays(-diff);
            }
            case "monthly":
            {
                int day = Math.Clamp(s.ResetDay, 1, 28);
                var thisMonth = new DateTime(now.Year, now.Month, day);
                return now.Date >= thisMonth ? thisMonth : thisMonth.AddMonths(-1);
            }
            default:
                return null;
        }
    }

    public static DateTime? NextReset(DateTime now, Settings s) => s.ResetPeriod switch
    {
        "daily" => CurrentStart(now, s)!.Value.AddDays(1),
        "weekly" => CurrentStart(now, s)!.Value.AddDays(7),
        "monthly" => CurrentStart(now, s)!.Value.AddMonths(1),
        _ => null,
    };

    public static bool IsValid(string period) => period is "daily" or "weekly" or "monthly" or "never";
}
