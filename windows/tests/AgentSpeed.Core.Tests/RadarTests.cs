using System.Text;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Radar;

namespace AgentSpeed.Core.Tests;

public sealed class RadarTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "spec", "fixtures", "radar", "codex-four-task-results.json"));
    private static RadarData Data => RadarParser.Scores(Fixture);
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PublicJsonReproducesPublishedTotalsAndMissingConfigurations()
    {
        var data = Data;
        Assert.Equal(8, data.Models.Length);
        Assert.Equal(46, data.Scores.Length);
        Assert.Equal(["ultra", "max", "xhigh", "high", "medium", "low"], data.Efforts);
        Assert.Equal(150, data.ScoreMax);
        var low = data.Scores.Single(s => s.Model == "gpt-6.1-sol" && s.Effort == "low");
        Assert.Equal(110.46964285714286, low.Score, 8);
        Assert.Equal(.5852596, low.CostUsd!.Value, 8);
        Assert.Equal(20.2376738863997, low.Seconds!.Value / 60, 8);
        var high = data.Scores.Single(s => s.Model == "gpt-6.1-sol" && s.Effort == "xhigh");
        Assert.Equal(137.99166666666667, high.Score, 8);
        Assert.DoesNotContain(data.Scores, s => s.Model == "gpt-6-luna" && s.Effort == "ultra");
        Assert.DoesNotContain(data.Scores, s => s.Model == "gpt-5.6-luna" && s.Effort == "ultra");
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("protocol")]
    [InlineData("configurations")]
    [InlineData("graded")]
    [InlineData("task_weights")]
    [InlineData("rows")]
    public void InvalidOrIncompleteRoundsAreRejected(string field)
    {
        var root = JsonNode.Parse(Fixture)!;
        if (field is "schema" or "protocol") root[field] = "future-schema";
        else if (field == "task_weights") root[field]!["combined"] = -1;
        else if (field == "rows") root[field]!.AsArray().RemoveAt(0);
        else root[field] = 0;
        Assert.Throws<FormatException>(() => RadarParser.Scores(root.ToJsonString()));
    }

    [Fact]
    public void UnknownCostStaysUnknownAndAZeroIsPreserved()
    {
        var root = JsonNode.Parse(Fixture)!;
        root["rows"]![0]!["combined"]!["cost_usd"] = null;
        root["rows"]![1]!["combined"]!["cost_usd"] = 0;
        var data = RadarParser.Scores(root.ToJsonString());
        Assert.Null(data.Scores.Single(s => s.Model == "gpt-6.1-sol" && s.Effort == "low").CostUsd);
        Assert.NotNull(data.Scores.Single(s => s.Model == "gpt-6.1-sol" && s.Effort == "medium").CostUsd);
        root["rows"]![1]!["combined"]!["cost_usd"] = -1;
        Assert.Throws<FormatException>(() => RadarParser.Scores(root.ToJsonString()));
    }

    [Fact]
    public void PicksFollowTheThreeRulesOnTheRealRound()
    {
        var picks = RadarRecommendations.Evaluate(Data);
        Assert.Equal([RadarTaskKind.Hard, RadarTaskKind.Daily, RadarTaskKind.Simple], picks.Select(p => p.Kind));
        Assert.Equal(("gpt-6.1-sol", "xhigh"), (picks[0].Score.Model, picks[0].Score.Effort));
        Assert.Equal(("gpt-6.1-sol", "low"), (picks[1].Score.Model, picks[1].Score.Effort));
        Assert.Equal(("gpt-6.1-terra", "low"), (picks[2].Score.Model, picks[2].Score.Effort));
        Assert.InRange(picks[1].Utility, .82655, .82657);
    }

    [Fact]
    public void RecommendationsRecomputeForNewModelsAndDoNotRecommendCheapWeakResults()
    {
        var data = Synthetic(Score("strong", 100, 10, 100), Score("balanced", 85, 1, 10),
            Score("cheap", 66, .01, 10), Score("weak", 64, 0, 1));
        var picks = RadarRecommendations.Evaluate(data);
        Assert.Equal(["strong", "balanced", "cheap"], picks.Select(p => p.Score.Model));
        var next = data with { Scores = [.. data.Scores, Score("new", 150, .1, 2)], Models = [.. data.Models, new("new", "New model")] };
        Assert.All(RadarRecommendations.Evaluate(next), p => Assert.Equal("new", p.Score.Model));
    }

    [Fact]
    public void UnknownMetricsAreExcludedOnlyWhenTheRuleNeedsThemAndZeroValuesAreSafe()
    {
        var data = Synthetic(Score("unknown", 100, null, null), Score("free", 90, 0, 0), Score("paid", 90, .1, 1));
        var picks = RadarRecommendations.Evaluate(data);
        Assert.Equal("unknown", picks[0].Score.Model);
        Assert.Equal("free", picks[1].Score.Model);
        Assert.Equal("free", picks[2].Score.Model);
        Assert.All(picks, p => Assert.True(double.IsFinite(p.Utility)));
        Assert.Single(RadarRecommendations.Evaluate(Synthetic(Score("only", 100, null, null))));
        Assert.Empty(RadarRecommendations.Evaluate(Synthetic(Score("zero", 0, 0, 0))));
    }

    [Fact]
    public void IdenticalNumbersKeepTheOriginalSnapshotAndChangedTimestamp()
    {
        var data = Data;
        var cache = new RadarCache(data, Now, Now, Now);
        var checkedAgain = RadarStore.Accept(cache, data with { SourceUpdated = "2026-10-28T23:00:00+08:00" }, Now.AddHours(3));
        Assert.Same(data, checkedAgain.Data);
        Assert.Equal(Now, checkedAgain.ChangedAt);
        Assert.Equal(Now.AddHours(3), checkedAgain.LastSuccess);
        var revised = data with { Scores = data.Scores.Select((s, i) => i == 0 ? s with { Score = s.Score + 1 } : s).ToArray() };
        var updated = RadarStore.Accept(checkedAgain, revised, Now.AddHours(6));
        Assert.Same(revised, updated.Data);
        Assert.Equal(Now.AddHours(6), updated.ChangedAt);
    }

    [Fact]
    public void ThreeHourScheduleSurvivesRestartAndRecoversFromAClockCorrection()
    {
        Assert.True(RadarStore.IsDue(Now, null));
        Assert.False(RadarStore.IsDue(Now.AddHours(3).AddTicks(-1), Now));
        Assert.True(RadarStore.IsDue(Now.AddHours(3), Now));
        Assert.True(RadarStore.IsDue(Now, Now.AddHours(1)));
        var directory = Path.Combine(Path.GetTempPath(), "agentspeed-radar-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RadarStore(directory);
            var cache = new RadarCache(Data, Now, Now, Now);
            store.Save(cache);
            var restored = new RadarStore(directory).Load();
            Assert.Equal(cache.Data!.Fingerprint(), restored.Data!.Fingerprint());
            Assert.False(RadarStore.IsDue(Now.AddHours(2), restored.LastAttempt));
            File.WriteAllText(Path.Combine(directory, "radar.json"), "{broken");
            Assert.Null(store.Load().Data);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(502)]
    public async Task ClientOnlyRequestsThePublicJsonAndNeverFallsBackToWebpage(int status)
    {
        var fake = new Transport(status);
        var client = new RadarClient(fake, "AgentSpeed/0.0.0 (Windows)");
        if (status == 200) Assert.Equal(46, (await client.FetchAsync(CancellationToken.None)).Scores.Length);
        else await Assert.ThrowsAsync<FormatException>(() => client.FetchAsync(CancellationToken.None));
        var request = Assert.Single(fake.Requests);
        Assert.Equal(RadarClient.ScoresUrl, request.Url);
        Assert.Equal("application/json", request.Accept);
        Assert.Equal(["codexradar.com"], request.AllowedHosts);
        Assert.False(request.Permits(new Uri("https://example.com/redirect")));
    }

    private static RadarScore Score(string model, double score, double? cost, double? seconds) => new(model, "high", score, cost, seconds, 0, 0, 0, 0);
    private static RadarData Synthetic(params RadarScore[] scores) => new(scores.Select(s => new RadarModel(s.Model, s.Model)).ToArray(), ["high"], 150, "test", null, scores);
    private sealed class Transport(int status) : IHttpTransport
    {
        public List<HttpFetchRequest> Requests { get; } = [];
        public Task<HttpFetchResponse> GetAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpFetchResponse(status, Encoding.UTF8.GetBytes(Fixture)));
        }
    }
}
