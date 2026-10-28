using AgentSpeed.App;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Windows.Tests;

public class CodexFollowTests
{
    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_1_x64__id\app\Codex.exe", true)]
    [InlineData(@"C:\Users\A\AppData\Local\Programs\Codex\Codex.exe", true)]
    [InlineData(@"C:\Users\A\AppData\Local\OpenAI\Codex\bin\hash\codex.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1_x64__id\app\ChatGPT.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_1_x64__id\app\resources\codex.exe", false)]
    [InlineData(@"C:\other\Codex.exe", false)]
    public void DetectsDesktopExecutableOnly(string path, bool expected) =>
        Assert.Equal(expected, CodexDesktop.IsDesktopExecutable(path));

    [Fact]
    public void WatcherLaunchesOnceUntilDesktopIsClosedAndReopened()
    {
        var gate = new CodexLaunchGate();
        Assert.False(gate.ShouldLaunch(CodexPresence.Absent));
        Assert.True(gate.ShouldLaunch(CodexPresence.Running));
        gate.MarkLaunched();
        Assert.False(gate.ShouldLaunch(CodexPresence.Running)); // a manual app exit is respected
        Assert.False(gate.ShouldLaunch(CodexPresence.Unknown));
        Assert.False(gate.ShouldLaunch(CodexPresence.Absent));
        Assert.True(gate.ShouldLaunch(CodexPresence.Running));
    }

    [Fact]
    public void AppQuitsOnlyAfterObservedDesktopDisappearsTwice()
    {
        var gate = new CodexExitGate();
        Assert.False(gate.ShouldQuit(CodexPresence.Absent)); // manual launch with Codex closed
        Assert.False(gate.ShouldQuit(CodexPresence.Running));
        Assert.False(gate.ShouldQuit(CodexPresence.Absent));
        Assert.False(gate.ShouldQuit(CodexPresence.Unknown)); // failed inspection cannot cause an exit
        Assert.False(gate.ShouldQuit(CodexPresence.Absent));
        Assert.True(gate.ShouldQuit(CodexPresence.Absent));
    }

    [Fact]
    public void AutomaticallyLaunchedAppExitsIfCodexClosesBeforeItFinishesStarting()
    {
        var gate = new CodexExitGate(seenRunning: true);
        Assert.False(gate.ShouldQuit(CodexPresence.Absent));
        Assert.True(gate.ShouldQuit(CodexPresence.Absent));
    }

    [Fact]
    public void FollowSettingDefaultsOffAndRoundTrips()
    {
        Assert.False(SettingsSerializer.Deserialize("{}").FollowCodex);
        Assert.False(SettingsSerializer.Deserialize("{\"followCodex\":\"yes\"}").FollowCodex);
        var settings = AppSettings.Default with { FollowCodex = true };
        Assert.True(SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings)).FollowCodex);
    }

    [Fact]
    public void StartupTaskIsInteractiveAndRunsWithoutTimeOrBatteryLimits()
    {
        var xml = CodexFollowStartup.TaskXml("S-1-5-21-test", @"C:\Program Files\Agent Speed\AgentSpeed.exe", @"C:\Users\A\settings.json");
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = Assert.IsType<System.Xml.Linq.XElement>(xml.Root);
        Assert.Equal("InteractiveToken", task.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "LogonType")?.Value);
        var settings = task.Element(ns + "Settings");
        Assert.Equal("IgnoreNew", settings?.Element(ns + "MultipleInstancesPolicy")?.Value);
        Assert.Equal("false", settings?.Element(ns + "DisallowStartIfOnBatteries")?.Value);
        Assert.Equal("false", settings?.Element(ns + "StopIfGoingOnBatteries")?.Value);
        Assert.Equal("PT0S", settings?.Element(ns + "ExecutionTimeLimit")?.Value);
        var exec = task.Element(ns + "Actions")?.Element(ns + "Exec");
        Assert.Equal(@"C:\Program Files\Agent Speed\AgentSpeed.exe", exec?.Element(ns + "Command")?.Value);
        Assert.Contains("--follow-settings", exec?.Element(ns + "Arguments")?.Value);
    }
}
