using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Core;
using AgentSpeed.Interop;
using AgentSpeed.Localization;
using AgentSpeed.Orb;
using AgentSpeed.Orb.Gpu;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Services;
using AgentSpeed.Settings.OrbsPane;
using AgentSpeed.Settings.Pages;
using AgentSpeed.Theme;

namespace AgentSpeed.RadarHarness;

internal static class OrbForegroundFlow
{
    public static int Run(string output)
    {
        if (!Path.IsPathFullyQualified(output)) return 2;
        Directory.CreateDirectory(output);
        var originalWindow = User32.GetForegroundWindow();
        var codexWindow = FindCodexWindow();
        Environment.SetEnvironmentVariable("AGENT_SPEED_SETTINGS_FILE", Path.Combine(output, "settings.json"));
        Environment.SetEnvironmentVariable("AGENT_SPEED_LOGS_DIR", Path.Combine(output, "logs"));
        var app = new AgentSpeed.App.App();
        var loc = new Localizer(Localizer.LoadEmbeddedTable(), "zh-Hans");
        LocalizationSource.Instance.Attach(loc);
        using var theme = new ThemeService(app.Dispatcher, AppearanceMode.Dark, forceReduceMotion: true);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AgentSpeed;component/Theme/Styles.xaml") });
        AppFonts.Apply(app.Resources, loc.LanguageCode);
        theme.EnableFluent();
        app.Startup += async (_, _) =>
        {
            var success = false;
            try
            {
                var nativeWpf = await VerifyRenderer(app, theme, loc, output, "wpf", codexWindow);
                if (!GpuDevice.Initialize()) throw new InvalidOperationException("GPU renderer unavailable.");
                var nativeGpu = await VerifyRenderer(app, theme, loc, output, "gpu", codexWindow);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true,
                    nativeCodexWindowIdentified = codexWindow != 0,
                    nativeCodexFocusVerified = nativeWpf && nativeGpu,
                    checks = new[] { "WPF and GPU hide/restore", "startup while hidden", "feeds and positions preserved", "settings while hidden", "manual visibility respected", "settings toggle binding", "native executable identity and timer" } }));
                success = true;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); }
            app.Shutdown(success ? 0 : 1);
        };
        try { return app.Run(); }
        finally { if (originalWindow != 0) User32.SetForegroundWindow(originalWindow); }
    }

    private static nint FindCodexWindow()
    {
        foreach (var name in new[] { "ChatGPT", "Codex" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    try
                    {
                        var window = process.MainWindowHandle;
                        if (CodexForeground.IsCodexWindow(window)) return window;
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        return 0;
    }

    private static async Task<bool> VerifyRenderer(AgentSpeed.App.App app, ThemeService theme, Localizer loc, string output, string renderer, nint codexWindow)
    {
        var nativeFocusVerified = false;
        using var settings = new SettingsService(app.Dispatcher);
        settings.MakeEphemeral();
        settings.Update(s => s with { ShowOrbs = true, OrbOnlyWhenCodexForeground = true, OrbNames = OrbNamesMode.Always,
            Orbs = new OrbsDocument([new OrbDefinition("foreground-a"), new OrbDefinition("foreground-b")]) });
        using var speed = new SpeedModel(new SpeedHub(new DispatcherSynchronizationContext(app.Dispatcher)), app.Dispatcher);
        var clock = new OrbClock(app.Dispatcher, () => speed.Now);
        OrbClock.Current = clock;
        using var manager = new OrbManager(app.Dispatcher, clock, settings, theme, speed, loc, new Host());
        manager.SetForegroundVisible(false);
        manager.Apply();
        await Idle(app);
        Check(manager.CaptureTargets().Count == 0, renderer + ": starting in background keeps new windows hidden");
        manager.SetForegroundVisible(true);
        await Idle(app);
        var centers = manager.Centers;
        var feeds = manager.Feeds;
        var document = settings.Current.Orbs;
        Check(manager.CaptureTargets().Count >= 2, renderer + ": configured orbs are visible");
        var foreground = false;
        using var monitor = new CodexForegroundMonitor(app.Dispatcher, manager.SetForegroundVisible, () => foreground);
        monitor.SetEnabled(true);
        await Idle(app);
        Check(manager.CaptureTargets().Count == 0, renderer + ": background hides orbs and name plates");
        Check(manager.Feeds.Count == 2 && manager.Feeds.All(p => ReferenceEquals(p.Value, feeds[p.Key])), renderer + ": feeds survive hiding");
        manager.Apply(); manager.OnDisplayChanged(); manager.Update(speed.Snapshot);
        await Idle(app);
        Check(manager.CaptureTargets().Count == 0, renderer + ": applying settings or data while hidden does not reveal windows");
        foreground = true; monitor.Refresh();
        await Idle(app);
        Check(manager.CaptureTargets().Count >= 2, renderer + ": foreground restores windows");
        Check(manager.Centers.All(p => centers.TryGetValue(p.Key, out var point) && point == p.Value), renderer + ": positions preserved");
        Check(settings.Current.Orbs == document && settings.Current.ShowOrbs, renderer + ": visibility preferences preserved");

        var model = new OrbsPaneViewModel(settings, manager, speed, loc);
        var page = new OrbPage { DataContext = new { Orbs = model } };
        var host = new Window { Width = 800, Height = 650, Content = new ScrollViewer { Content = page } };
        host.Show(); await Idle(app);
        var toggle = (CheckBox)page.FindName("CodexForegroundToggle");
        toggle.IsChecked = false;
        Check(!settings.Current.OrbOnlyWhenCodexForeground, renderer + ": toggle persists off");
        toggle.IsChecked = true;
        Check(settings.Current.OrbOnlyWhenCodexForeground, renderer + ": toggle persists on");
        if (renderer == "wpf") Program.Capture((FrameworkElement)host.Content, Path.Combine(output, "orb-settings.png"));
        monitor.SetEnabled(false);
        using (var nativeMonitor = new CodexForegroundMonitor(app.Dispatcher, manager.SetForegroundVisible))
        {
            Check(!CodexForeground.IsCodexWindow(new WindowInteropHelper(host).Handle), renderer + ": another app is not Codex");
            nativeMonitor.SetEnabled(true);
            host.Activate();
            await Wait(() => !CodexForeground.Read() && manager.CaptureTargets().Count == 0);
            Check(manager.CaptureTargets().Count == 0, renderer + ": real foreground window hides orbs through timer");
            if (codexWindow != 0)
            {
                Check(CodexForeground.IsCodexWindow(codexWindow), renderer + ": real Codex desktop executable identified");
                if (User32.SetForegroundWindow(codexWindow))
                {
                    await Wait(() => CodexForeground.Read() && manager.CaptureTargets().Count >= 2);
                    Check(manager.CaptureTargets().Count >= 2, renderer + ": real Codex focus restores orbs through timer");
                    nativeFocusVerified = true;
                }
                else Console.WriteLine("SKIP " + renderer + ": Windows foreground lock prevents automated focus switching; lifecycle verified with injected focus.");
            }
        }
        monitor.SetEnabled(true);
        host.Close();

        settings.Update(s => s with { Orbs = OrbEditing.SetVisible(s.Orbs, "foreground-b", false) });
        manager.Apply();
        foreground = false; monitor.Refresh(); foreground = true; monitor.Refresh();
        Check(manager.Feeds.Count == 1, renderer + ": manually hidden orb stays hidden");
        settings.Update(s => s.WithShowOrbs(false)); manager.Apply();
        foreground = false; monitor.Refresh(); foreground = true; monitor.Refresh();
        Check(manager.CaptureTargets().Count == 0 && !settings.Current.ShowOrbs, renderer + ": master off stays off");
        settings.Update(s => s.WithShowOrbs(true)); manager.Apply();
        foreground = false; monitor.Refresh(); monitor.SetEnabled(false);
        Check(manager.CaptureTargets().Count >= 2, renderer + ": disabling foreground mode restores ordinary visibility");
        return nativeFocusVerified;
    }
    private static async Task Idle(AgentSpeed.App.App app) => await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(4);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(80);
        if (!condition()) throw new InvalidOperationException("Native focus transition timed out.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
    private sealed class Host : IOrbHost
    {
        public void ShowDashboardAt(POINT anchor) { }
        public void ShowOrbMenu(string orbId) { }
        public void OpenOrbSettings(string orbId) { }
    }
}
