#if DEBUG
using System.ComponentModel;
using System.Diagnostics;

namespace AgentSpeed.App;

/// <summary>Isolates lifecycle tests from an installed instance and the real Codex app. Never compiled into releases.</summary>
internal static class CodexFollowTestHooks
{
    private static string? TestId => Guid.TryParse(Environment.GetEnvironmentVariable("AGENT_SPEED_TEST_INSTANCE"), out var id)
        && Path.IsPathFullyQualified(Environment.GetEnvironmentVariable("AGENT_SPEED_SETTINGS_FILE") ?? "") ? id.ToString("N") : null;

    public static bool IsActive => TestId is not null;
    public static string Suffix => TestId is { } id ? $".test.{id}" : "";
    public static string? DesktopPath => IsActive && Environment.GetEnvironmentVariable("AGENT_SPEED_TEST_CODEX_PATH") is { } path
        && Path.IsPathFullyQualified(path) ? path : null;

    public static string WatcherArguments
    {
        get
        {
            if (TestId is not { } id || DesktopPath is not { } desktop) { return ""; }
            var logs = Environment.GetEnvironmentVariable("AGENT_SPEED_LOGS_DIR") ?? "";
            var history = Environment.GetEnvironmentVariable("AGENT_SPEED_HISTORY_FILE") ?? "";
            var report = Environment.GetEnvironmentVariable("AGENT_SPEED_REPORT_DIR") ?? "";
            if (desktop.Contains('"') || logs.Contains('"') || history.Contains('"') || report.Contains('"')) { return ""; }
            return $" --follow-test-id {id} --follow-test-desktop \"{desktop}\" --follow-test-logs \"{logs}\""
                + $" --follow-test-history \"{history}\" --follow-test-report \"{report}\"";
        }
    }

    public static void InitializeWatcher(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var id = Value("--follow-test-id");
        var desktop = Value("--follow-test-desktop");
        var settings = Value("--follow-settings");
        if (!Guid.TryParse(id, out _) || desktop is null || !Path.IsPathFullyQualified(desktop)
            || settings is null || !Path.IsPathFullyQualified(settings)) { return; }
        Environment.SetEnvironmentVariable("AGENT_SPEED_TEST_INSTANCE", id);
        Environment.SetEnvironmentVariable("AGENT_SPEED_TEST_CODEX_PATH", desktop);
        Environment.SetEnvironmentVariable("AGENT_SPEED_SETTINGS_FILE", settings);
        var logs = Value("--follow-test-logs");
        if (!string.IsNullOrWhiteSpace(logs) && Path.IsPathFullyQualified(logs))
        {
            Environment.SetEnvironmentVariable("AGENT_SPEED_LOGS_DIR", logs);
        }
        var history = Value("--follow-test-history");
        if (!string.IsNullOrWhiteSpace(history) && Path.IsPathFullyQualified(history))
        {
            Environment.SetEnvironmentVariable("AGENT_SPEED_HISTORY_FILE", history);
        }
        var report = Value("--follow-test-report");
        if (!string.IsNullOrWhiteSpace(report) && Path.IsPathFullyQualified(report))
        {
            Environment.SetEnvironmentVariable("AGENT_SPEED_REPORT_DIR", report);
        }
    }

    public static CodexPresence ReadDesktop(string path)
    {
        var unknown = false;
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == session
                        && string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                    {
                        return CodexPresence.Running;
                    }
                }
                catch (Win32Exception) { unknown = true; }
                catch (InvalidOperationException) { }
            }
        }

        return unknown ? CodexPresence.Unknown : CodexPresence.Absent;
    }
}
#endif
