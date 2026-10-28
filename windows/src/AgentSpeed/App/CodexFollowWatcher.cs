using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.App;

internal static class CodexFollowSettings
{
    // Read without SettingsStore.Load(): a background reader must never rename the user's settings file.
    public static bool? Read(string? settingsFile = null)
    {
        try
        {
            var file = settingsFile ?? AppPaths.SettingsFile;
            if (!File.Exists(file)) { return false; }
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > SettingsSerializer.MaxFileBytes) { return null; }
            using var reader = new StreamReader(stream);
            return SettingsSerializer.Deserialize(reader.ReadToEnd()).FollowCodex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>A separate, single background process, started at sign-in through Task Scheduler.</summary>
internal static class CodexFollowWatcher
{
    private const int PollMilliseconds = 1500;

    private static string MutexName
    {
        get
        {
            var name = @"Local\AgentSpeed.CodexFollow";
#if DEBUG
            name += CodexFollowTestHooks.Suffix;
#endif
            return name;
        }
    }

    internal static bool WaitForRunning(bool expected, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var running = Mutex.TryOpenExisting(MutexName, out var existing);
            existing?.Dispose();
            if (running == expected) { return true; }
            Thread.Sleep(100);
        }
        while (timer.Elapsed < timeout);
        return false;
    }

    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--follow-settings");
        var settingsFile = index >= 0 && index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1])
            && !args[index + 1].StartsWith(@"\\", StringComparison.Ordinal) ? args[index + 1] : AppPaths.SettingsFile;
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created) { return 0; }
        AppLog.Info("Codex follow watcher started.");
        var gate = new CodexLaunchGate();
        try
        {
            while (true)
            {
                var enabled = CodexFollowSettings.Read(settingsFile);
                if (enabled == false) { break; }
                if (enabled == true && gate.ShouldLaunch(CodexDesktop.Read()) && StartApp(settingsFile)) { gate.MarkLaunched(); }
                Thread.Sleep(PollMilliseconds);
            }
        }
        finally
        {
            mutex.ReleaseMutex();
            AppLog.Info("Codex follow watcher stopped.");
        }

        return 0;
    }

    private static bool StartApp(string settingsFile)
    {
        var path = AppPaths.ExecutablePath;
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        try
        {
            var info = new ProcessStartInfo(path) { UseShellExecute = false };
            info.ArgumentList.Add("--follow-codex-ui");
            info.Environment["AGENT_SPEED_SETTINGS_FILE"] = settingsFile;
            using var process = Process.Start(info);
            if (process is null) { return false; }
            AppLog.Info($"Codex follow: started Agent Speed (pid {process.Id}).");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn("Codex follow: could not start Agent Speed; will retry.", ex);
            return false;
        }
    }
}

/// <summary>Allows the UI to exit normally, including flushing settings and stopping its services.</summary>
internal sealed class CodexFollowExitMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action _quit;
    private CodexExitGate _gate = new();

    public CodexFollowExitMonitor(Dispatcher dispatcher, Action quit)
    {
        _quit = quit;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) => Check(), dispatcher);
        _timer.Stop();
    }

    public void SetEnabled(bool enabled, bool seenRunning = false)
    {
        _timer.Stop();
        _gate = new CodexExitGate(seenRunning);
        if (enabled)
        {
            Check();
            _timer.Start();
        }
    }

    private void Check()
    {
        if (!_gate.ShouldQuit(CodexDesktop.Read())) { return; }
        _timer.Stop();
        AppLog.Info("Codex desktop exited; closing Agent Speed.");
        _quit();
    }

    public void Dispose() => _timer.Stop();
}
