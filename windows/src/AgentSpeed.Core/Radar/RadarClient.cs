using System.Text;
using AgentSpeed.Core.Network;

namespace AgentSpeed.Core.Radar;

public sealed class RadarClient(IHttpTransport transport, string userAgent)
{
    public static readonly Uri ScoresUrl = new("https://codexradar.com/data/codex-four-task-results.json");
    public const int MaxBytes = 4 * 1024 * 1024;
    private static readonly IReadOnlySet<string> Hosts = new HashSet<string>(StringComparer.Ordinal) { "codexradar.com" };

    public async Task<RadarData> FetchAsync(CancellationToken token)
    {
        var response = await transport.GetAsync(new HttpFetchRequest(ScoresUrl, userAgent, MaxBytes, Hosts), token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!response.IsSuccess || response.Body.Length > MaxBytes)
            throw new FormatException("Radar JSON request failed: " + response.Status);
        return RadarParser.Scores(Encoding.UTF8.GetString(response.Body));
    }
}
