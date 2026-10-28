using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSpeed.Core.Radar;

public sealed record RadarModel(string Id, string Name);
public sealed record RadarScore(string Model, string Effort, double Score, double? CostUsd, double? Seconds,
    double Combined, double Pompeii, double Pelican, double Candy);
public sealed record RadarData(RadarModel[] Models, string[] Efforts, double ScoreMax, string RoundId,
    string? SourceUpdated, RadarScore[] Scores)
{
    // Retrieval times and upstream verification timestamps do not make identical scores into new results.
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(this with { SourceUpdated = null }, RadarJson.Default.RadarData)));
}

public sealed record RadarCache(RadarData? Data, DateTimeOffset? LastAttempt, DateTimeOffset? LastSuccess,
    DateTimeOffset? ChangedAt);

[JsonSerializable(typeof(RadarData))]
[JsonSerializable(typeof(RadarCache))]
internal sealed partial class RadarJson : JsonSerializerContext;
