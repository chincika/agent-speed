using System.Text.Json;

namespace AgentSpeed.Core.Radar;

/// <summary>Versioned public JSON only. Incomplete/unsupported rounds never replace the last good snapshot.</summary>
public static class RadarParser
{
    private static readonly string[] Tasks = ["combined", "pompeii", "pelican", "candy"];

    public static RadarData Scores(string json)
    {
        try { return Read(json); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new FormatException("Invalid radar JSON.", ex); }
    }

    private static RadarData Read(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
        var root = document.RootElement;
        if (Text(root, "schema") != "codex-four-task-results/1"
            || Text(root, "protocol") != "codex-four-weighted-150-v2") throw new FormatException("Unsupported radar schema or protocol.");
        var maximum = Number(root, "score_max") ?? throw new FormatException("Missing score maximum.");
        if (maximum is <= 0 or > 10000) throw new FormatException("Invalid score maximum.");
        var weights = Tasks.Select(t => Number(root.GetProperty("task_weights"), t)
            ?? throw new FormatException("Missing task weight.")).ToArray();
        if (weights.Any(n => n < 0) || Math.Abs(weights.Sum() - maximum) > .001)
            throw new FormatException("Invalid task weights.");
        var scores = new List<RadarScore>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            var model = Required(row, "model");
            var effort = Required(row, "effort");
            if (!keys.Add(model + ":" + effort)) throw new FormatException("Duplicate model/effort.");
            var parts = Tasks.Select(t => row.GetProperty(t)).ToArray();
            var values = new[]
            {
                Number(parts[0], "score") ?? throw new FormatException("Missing combined score."),
                Number(parts[1], "score100") ?? (Number(parts[1], "reward") * 100) ?? throw new FormatException("Missing Pompeii score."),
                Number(parts[2], "score") ?? throw new FormatException("Missing pelican score."),
                parts[3].GetProperty("correct").ValueKind switch
                {
                    JsonValueKind.True => 100d, JsonValueKind.False => 0d,
                    _ => throw new FormatException("Missing candy result."),
                },
            };
            if (values.Any(n => n is < 0 or > 100)) throw new FormatException("Invalid task score.");
            double? Sum(string name)
            {
                var numbers = parts.Select(p => Number(p, name)).ToArray();
                if (numbers.Any(n => n < 0)) throw new FormatException("Negative cost or duration.");
                if (!numbers.All(n => n.HasValue)) return null;
                var sum = numbers.Sum(n => n!.Value);
                if (!double.IsFinite(sum)) throw new FormatException("Invalid measurement total.");
                return sum;
            }
            scores.Add(new RadarScore(model, effort, values.Select((v, i) => v * weights[i] / 100).Sum(),
                Sum("cost_usd"), Sum("model_elapsed_seconds"), values[0], values[1], values[2], values[3]));
            if (scores.Count > 2000) throw new FormatException("Too many score rows.");
        }
        if (scores.Count == 0) throw new FormatException("No radar scores.");
        if (root.GetProperty("configurations").GetInt32() != scores.Count
            || root.GetProperty("graded").GetInt32() != root.GetProperty("expected").GetInt32()
            || root.GetProperty("expected").GetInt32() != scores.Count * Tasks.Length)
            throw new FormatException("Incomplete score snapshot.");
        var ids = root.GetProperty("models").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        if (ids.Length is 0 or > 100 || ids.Any(s => s.Length is 0 or > 200)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
            || scores.Any(s => !ids.Contains(s.Model, StringComparer.Ordinal))) throw new FormatException("Invalid models.");
        var names = root.GetProperty("model_names");
        var modelRows = ids.Select(id => new RadarModel(id, Required(names, id))).ToArray();
        var efforts = root.GetProperty("efforts").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        if (efforts.Length is 0 or > 20 || efforts.Any(e => e.Length is 0 or > 40)
            || efforts.Distinct().Count() != efforts.Length || scores.Any(s => !efforts.Contains(s.Effort)))
            throw new FormatException("Invalid efforts.");
        var ordered = modelRows.SelectMany(m => efforts.Select(e => scores.FirstOrDefault(s => s.Model == m.Id && s.Effort == e)))
            .Where(s => s is not null).Cast<RadarScore>().ToArray();
        return new RadarData(modelRows, efforts, maximum, Required(root, "round_id"),
            Text(root, "score_snapshot_at") ?? Text(root, "verified_at"), ordered);
    }

    public static void ValidateCached(RadarData data)
    {
        if (data.Models.Length is 0 or > 100 || data.Efforts.Length is 0 or > 20 || data.Scores.Length is 0 or > 2000
            || !double.IsFinite(data.ScoreMax) || data.ScoreMax <= 0 || data.ScoreMax > 10000
            || data.Models.Select(m => m.Id).Distinct().Count() != data.Models.Length
            || data.Efforts.Distinct().Count() != data.Efforts.Length || data.Efforts.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 40)
            || data.Models.Any(m => string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 200)
            || data.Scores.Select(s => s.Model + ":" + s.Effort).Distinct().Count() != data.Scores.Length
            || data.Scores.Any(s => !double.IsFinite(s.Score) || s.Score < 0 || s.Score > data.ScoreMax
                || !data.Models.Any(m => m.Id == s.Model) || !data.Efforts.Contains(s.Effort)
                || s.CostUsd is { } c && (!double.IsFinite(c) || c < 0)
                || s.Seconds is { } t && (!double.IsFinite(t) || t < 0)
                || new[] { s.Combined, s.Pompeii, s.Pelican, s.Candy }.Any(v => !double.IsFinite(v) || v < 0 || v > 100)))
            throw new FormatException("Invalid radar cache.");
    }

    private static string Required(JsonElement element, string name) => Text(element, name) is { Length: > 0 and <= 200 } text
        ? text : throw new FormatException("Missing radar identifier.");
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetDouble(out var n) || !double.IsFinite(n)) throw new FormatException("Invalid radar number.");
        return n;
    }
}
