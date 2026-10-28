using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Windows.Tests;

public class CodexForegroundTests
{
    [Fact]
    public void PreferenceDefaultsOffAndPersistsWithoutChangingMasterOrIndividualVisibility()
    {
        Assert.False(SettingsSerializer.Deserialize("{}").OrbOnlyWhenCodexForeground);
        Assert.False(SettingsSerializer.Deserialize("{\"orbOnlyWhenCodexForeground\":\"yes\"}").OrbOnlyWhenCodexForeground);
        var original = AppSettings.Default.WithShowOrbs(false) with { OrbOnlyWhenCodexForeground = true };
        var restored = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(original));
        Assert.True(restored.OrbOnlyWhenCodexForeground);
        Assert.False(restored.ShowOrbs);
        Assert.Equal(original.Orbs, restored.Orbs);
    }

    [Fact]
    public void FocusSwitchesHideAndRestoreOnlyOnTransitions() => OnSta(() =>
    {
        var foreground = false;
        var applied = new List<bool>();
        using var monitor = new CodexForegroundMonitor(Dispatcher.CurrentDispatcher, applied.Add, () => foreground);
        monitor.SetEnabled(true);
        monitor.Refresh();
        Assert.Equal([false], applied);
        foreground = true;
        monitor.Refresh(); monitor.Refresh();
        Assert.Equal([false, true], applied);
        foreground = false;
        monitor.Refresh();
        Assert.Equal([false, true, false], applied);
        monitor.SetEnabled(false);
        monitor.Refresh();
        Assert.Equal([false, true, false, true], applied);
        monitor.Dispose();
        monitor.SetEnabled(true); monitor.Refresh();
        Assert.Equal(4, applied.Count);
    });

    [Fact]
    public void DisabledPreferenceDoesNotInspectForegroundOrHideAnything() => OnSta(() =>
    {
        var reads = 0;
        var applied = new List<bool>();
        using var monitor = new CodexForegroundMonitor(Dispatcher.CurrentDispatcher, applied.Add, () => { reads++; return false; });
        monitor.SetEnabled(false); monitor.Refresh();
        Assert.Equal(0, reads);
        Assert.Equal([true], applied);
        Assert.False(CodexForeground.IsCodexWindow(0));
    });

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
