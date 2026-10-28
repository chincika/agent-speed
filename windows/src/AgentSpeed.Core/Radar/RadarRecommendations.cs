namespace AgentSpeed.Core.Radar;

public enum RadarTaskKind { Hard, Daily, Simple }
public sealed record RadarRecommendation(RadarTaskKind Kind, RadarScore Score, double Utility);

/// <summary>Local recommendations; every model/effort competes against relative quality floors for this round.</summary>
public static class RadarRecommendations
{
    public const double DailyFloor = .80;
    public const double SimpleFloor = .65;

    public static RadarRecommendation[] Evaluate(RadarData data)
    {
        if (data.Scores.Length == 0) return [];
        var best = data.Scores.Max(s => s.Score);
        if (best <= 0) return [];
        var picks = new List<RadarRecommendation>();
        var hard = data.Scores.OrderByDescending(s => s.Score).ThenBy(s => s.CostUsd ?? double.MaxValue)
            .ThenBy(s => s.Seconds ?? double.MaxValue).ThenBy(s => s.Model, StringComparer.Ordinal).ThenBy(s => s.Effort, StringComparer.Ordinal).First();
        picks.Add(new(RadarTaskKind.Hard, hard, 1));
        var daily = data.Scores.Where(s => s.Score >= best * DailyFloor && s.CostUsd.HasValue && s.Seconds.HasValue).ToArray();
        if (daily.Length > 0)
        {
            var minCost = daily.Min(s => s.CostUsd!.Value);
            var minSeconds = daily.Min(s => s.Seconds!.Value);
            // A weighted geometric mean; common offsets handle a measured zero cost/time safely.
            double Utility(RadarScore s) => Math.Pow(s.Score / best, .50)
                * Math.Pow((minCost + .01) / (s.CostUsd!.Value + .01), .30)
                * Math.Pow((minSeconds + 1) / (s.Seconds!.Value + 1), .20);
            var winner = daily.OrderByDescending(Utility).ThenByDescending(s => s.Score).ThenBy(s => s.CostUsd)
                .ThenBy(s => s.Seconds).ThenBy(s => s.Model, StringComparer.Ordinal).ThenBy(s => s.Effort, StringComparer.Ordinal).First();
            picks.Add(new(RadarTaskKind.Daily, winner, Utility(winner)));
        }
        var simple = data.Scores.Where(s => s.Score >= best * SimpleFloor && s.CostUsd.HasValue)
            .OrderBy(s => s.CostUsd).ThenByDescending(s => s.Score).ThenBy(s => s.Seconds ?? double.MaxValue)
            .ThenBy(s => s.Model, StringComparer.Ordinal).ThenBy(s => s.Effort, StringComparer.Ordinal).FirstOrDefault();
        if (simple is not null) picks.Add(new(RadarTaskKind.Simple, simple, simple.Score / best));
        return picks.ToArray();
    }
}
