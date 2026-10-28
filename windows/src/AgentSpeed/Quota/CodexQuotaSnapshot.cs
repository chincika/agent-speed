using System.Globalization;
using System.Text.Json;

namespace AgentSpeed.Quota;

/// <summary>Remaining ChatGPT plan quota, identified by duration rather than primary/secondary order.</summary>
internal sealed record CodexQuotaSnapshot(double? FiveHourRemaining, double? WeekRemaining, DateTimeOffset? ReadAt = null)
{
    public static CodexQuotaSnapshot Unknown { get; } = new(null, null);

    public static string Percent(double? remaining) => remaining is { } value
        ? Math.Floor(value).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";

    public static CodexQuotaSnapshot Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return Unknown;
        JsonElement bucket;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets)
            && buckets.ValueKind == JsonValueKind.Object && buckets.TryGetProperty("codex", out var codex)
            && codex.ValueKind == JsonValueKind.Object)
        {
            bucket = codex;
        }
        else if (result.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object
            && (!legacy.TryGetProperty("limitId", out var id) || id.ValueKind == JsonValueKind.Null
                || (id.ValueKind == JsonValueKind.String && id.GetString() == "codex")))
        {
            bucket = legacy;
        }
        else
        {
            return Unknown;
        }

        double? fiveHour = null, week = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!bucket.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("windowDurationMins", out var duration) || duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out var minutes)
                || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent))
                continue;

            var remaining = Math.Clamp(100 - percent, 0, 100);
            if (minutes == 300) fiveHour = remaining;
            if (minutes == 10080) week = remaining;
        }

        return new(fiveHour, week, DateTimeOffset.UtcNow);
    }
}
