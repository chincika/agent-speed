using System.Text.Json;
using System.IO;
using AgentSpeed.Quota;
using AgentSpeed.Taskbar;

namespace AgentSpeed.Windows.Tests;

public class CodexQuotaTests
{
    private static CodexQuotaSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CodexQuotaSnapshot.Parse(document.RootElement);
    }

    [Fact]
    public void UsesCodexBucketAndIdentifiesReversedWindowsByDuration()
    {
        var value = Parse("""
            {"rateLimits":{"primary":{"usedPercent":99,"windowDurationMins":300}},
             "rateLimitsByLimitId":{"other":{"primary":{"usedPercent":88,"windowDurationMins":300}},
             "codex":{"primary":{"usedPercent":11,"windowDurationMins":10080},"secondary":{"usedPercent":69,"windowDurationMins":300}}}}
            """);
        Assert.Equal(31, value.FiveHourRemaining);
        Assert.Equal(89, value.WeekRemaining);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"rateLimits\":null}")]
    [InlineData("{\"rateLimits\":{\"limitId\":\"codex_other\",\"primary\":{\"usedPercent\":12,\"windowDurationMins\":300}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":null,\"windowDurationMins\":300}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":12,\"windowDurationMins\":15}}}")]
    public void MissingAndUnrelatedLimitsAreUnknown(string json)
    {
        var value = Parse(json);
        Assert.Null(value.FiveHourRemaining);
        Assert.Null(value.WeekRemaining);
        Assert.Equal("—", CodexQuotaSnapshot.Percent(value.FiveHourRemaining));
    }

    [Fact]
    public void LegacyFallbackClampsAndRetainsUsableWindows()
    {
        var value = Parse("""{"rateLimits":{"primary":{"usedPercent":105,"windowDurationMins":300},"secondary":{"usedPercent":-4,"windowDurationMins":10080}}}""");
        Assert.Equal(0, value.FiveHourRemaining);
        Assert.Equal(100, value.WeekRemaining);
        value = Parse("""{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300},"secondary":{"usedPercent":null,"windowDurationMins":10080}}}""");
        Assert.Equal(100, value.FiveHourRemaining);
        Assert.Null(value.WeekRemaining);
    }

    [Fact]
    public async Task DoesNothingUntilEnabledThenRefreshesAndStopsOnDisable()
    {
        var reads = 0;
        using var service = new CodexQuotaService(() => { }, _ =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(new CodexQuotaSnapshot(31, 89));
        }, TimeSpan.FromMilliseconds(35));
        Assert.Equal(TimeSpan.FromMinutes(3), CodexQuotaService.RefreshInterval);
        await Task.Delay(80);
        Assert.Equal(0, reads);
        service.SetEnabled(true);
        await Until(() => Volatile.Read(ref reads) >= 2);
        service.SetEnabled(false);
        var stopped = Volatile.Read(ref reads);
        await Task.Delay(120);
        Assert.Equal(stopped, reads);
        Assert.Equal(CodexQuotaSnapshot.Unknown, service.Snapshot);
    }

    [Fact]
    public async Task DisableCancelsReadAndRejectsLateResults()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        using var service = new CodexQuotaService(() => { }, async token =>
        {
            using var registration = token.Register(() => cancelled = true);
            entered.TrySetResult();
            await release.Task;
            return new(31, 89);
        });
        service.SetEnabled(true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.SetEnabled(false);
        Assert.True(cancelled);
        release.SetResult();
        await Task.Delay(80);
        Assert.Equal(CodexQuotaSnapshot.Unknown, service.Snapshot);
    }

    [Fact]
    public async Task FailedReadClearsOldValuesAndNextRefreshRecovers()
    {
        var reads = 0;
        using var service = new CodexQuotaService(() => { }, _ => Interlocked.Increment(ref reads) == 1
            ? throw new IOException("offline") : Task.FromResult(new CodexQuotaSnapshot(10, 80)), TimeSpan.FromMilliseconds(40));
        service.SetEnabled(true);
        await Until(() => Volatile.Read(ref reads) > 0);
        Assert.Equal(CodexQuotaSnapshot.Unknown, service.Snapshot);
        await Until(() => service.Snapshot.FiveHourRemaining == 10);
    }

    [Fact]
    public void PercentUpdatesDoNotChangeLayoutButToggleDoes()
    {
        var state = TaskbarState.Initial with { Quota = new("5 小时", "周额度", "100%", "100%") };
        Assert.False(state.LayoutDiffers(state with { Quota = state.Quota with { FiveHourValue = "9%" } }));
        Assert.True(state.LayoutDiffers(state with { Quota = null }));
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
