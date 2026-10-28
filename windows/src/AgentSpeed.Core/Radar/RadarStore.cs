using System.Text.Json;

namespace AgentSpeed.Core.Radar;

/// <summary>Last good normalized data, replaced atomically. No browsing state, user activity or credentials.</summary>
public sealed class RadarStore(string directory)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(3);
    private string FilePath => Path.Combine(directory, "radar.json");

    public RadarCache Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > RadarClient.MaxBytes) return new(null, null, null, null);
            var cache = JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), RadarJson.Default.RadarCache);
            if (cache?.Data is { } data) RadarParser.ValidateCached(data);
            return cache ?? new(null, null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException
            or NullReferenceException or ArgumentException) { return new(null, null, null, null); }
    }

    public void Save(RadarCache cache)
    {
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(cache, RadarJson.Default.RadarCache));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static bool IsDue(DateTimeOffset now, DateTimeOffset? lastAttempt) => lastAttempt is null
        || now - lastAttempt >= RefreshInterval || lastAttempt > now + TimeSpan.FromMinutes(5);

    public static RadarCache Accept(RadarCache cache, RadarData data, DateTimeOffset now)
    {
        var changed = cache.Data?.Fingerprint() != data.Fingerprint();
        return new RadarCache(changed ? data : cache.Data, now, now,
            changed ? now : cache.ChangedAt);
    }
}
