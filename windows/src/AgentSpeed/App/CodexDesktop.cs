using System.ComponentModel;
using System.Diagnostics;

namespace AgentSpeed.App;

internal enum CodexPresence { Unknown, Absent, Running }

/// <summary>Finds the desktop app, not Codex CLI or a different ChatGPT installation.</summary>
internal static class CodexDesktop
{
    internal static bool IsDesktopExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) { return false; }
        var file = parts[^1];
        if (!file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
            && !file.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)) { return false; }

        // The MSIX app can call its executable ChatGPT.exe; its CLI lives below app\resources instead.
        if (parts[^2].Equals("app", StringComparison.OrdinalIgnoreCase)
            && parts[^3].StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) { return true; }

        // Per-user unpackaged desktop installs. Do not match OpenAI\Codex\bin\codex.exe (the CLI).
        return parts[^2].Equals("Codex", StringComparison.OrdinalIgnoreCase)
            && parts[^3].Equals("Programs", StringComparison.OrdinalIgnoreCase);
    }

    public static CodexPresence Read()
    {
#if DEBUG
        if (CodexFollowTestHooks.DesktopPath is { } testPath)
        {
            return CodexFollowTestHooks.ReadDesktop(testPath);
        }
#endif
        var uncertain = false;
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        foreach (var name in new[] { "ChatGPT", "Codex" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != session) { continue; }
                        var path = process.MainModule?.FileName;
                        if (path is null) { uncertain = true; }
                        else if (IsDesktopExecutable(path)) { return CodexPresence.Running; }
                    }
                    catch (Win32Exception) { uncertain = true; }
                    catch (InvalidOperationException) { /* Process exited during inspection. */ }
                    catch (NotSupportedException) { uncertain = true; }
                }
            }
        }

        return uncertain ? CodexPresence.Unknown : CodexPresence.Absent;
    }
}

/// <summary>Launch once per desktop session, including when the watcher itself starts after Codex.</summary>
internal sealed class CodexLaunchGate
{
    private bool _launched;

    public bool ShouldLaunch(CodexPresence presence)
    {
        if (presence == CodexPresence.Absent) { _launched = false; }
        return presence == CodexPresence.Running && !_launched;
    }

    public void MarkLaunched() => _launched = true;
}

/// <summary>Do not quit for an unreadable snapshot or before ever observing a running desktop app.</summary>
internal sealed class CodexExitGate(bool seenRunning = false)
{
    private bool _seenRunning = seenRunning;
    private int _absentSamples;

    public bool ShouldQuit(CodexPresence presence)
    {
        if (presence == CodexPresence.Running)
        {
            _seenRunning = true;
            _absentSamples = 0;
        }
        else if (presence == CodexPresence.Unknown)
        {
            _absentSamples = 0;
        }
        else if (_seenRunning && ++_absentSamples >= 2)
        {
            return true;
        }

        return false;
    }
}
