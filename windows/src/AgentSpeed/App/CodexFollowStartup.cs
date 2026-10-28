using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AgentSpeed.App;

/// <summary>Registers a per-user, interactive watcher outside Codex's MSIX registry isolation.</summary>
internal static class CodexFollowStartup
{
    private static string TaskName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var name = $"Agent Speed - Codex Follow - {identity.User?.Value ?? Environment.UserName}";
#if DEBUG
            name += CodexFollowTestHooks.Suffix;
#endif
            return name;
        }
    }

    public static bool Register()
    {
        var path = AppPaths.ExecutablePath;
        var settingsFile = AppPaths.SettingsFile;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('"', StringComparison.Ordinal)
            || settingsFile.Contains('"', StringComparison.Ordinal)) { return false; }
        var xmlFile = Path.Combine(Path.GetTempPath(), $"agentspeed-follow-{Guid.NewGuid():N}.xml");
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User?.Value ?? Environment.UserName;
            var document = TaskXml(user, path, settingsFile);
            using (var writer = XmlWriter.Create(xmlFile, new XmlWriterSettings { Encoding = Encoding.Unicode, Indent = true }))
            {
                document.Save(writer);
            }

            return Run("/Create", "/XML", xmlFile, "/TN", TaskName, "/F");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn("Codex follow: could not prepare the startup task.", ex);
            return false;
        }
        finally
        {
            try { File.Delete(xmlFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static XDocument TaskXml(string user, string executable, string settingsFile)
    {
        var arguments = $"--follow-codex --follow-settings \"{settingsFile}\"";
#if DEBUG
        arguments += CodexFollowTestHooks.WatcherArguments;
#endif
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XDocument(new XDeclaration("1.0", "utf-16", null),
            new XElement(ns + "Task", new XAttribute("version", "1.4"),
                new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger",
                    new XElement(ns + "Enabled", true), new XElement(ns + "UserId", user))),
                new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(ns + "UserId", user), new XElement(ns + "LogonType", "InteractiveToken"),
                    new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", false),
                    new XElement(ns + "StopIfGoingOnBatteries", false),
                    new XElement(ns + "StartWhenAvailable", true),
                    new XElement(ns + "AllowStartOnDemand", true), new XElement(ns + "Enabled", true),
                    new XElement(ns + "Hidden", true), new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "RestartOnFailure", new XElement(ns + "Interval", "PT1M"), new XElement(ns + "Count", 3))),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec", new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments", arguments)))));
    }

    public static bool Start()
    {
        if (!Run("/Run", "/TN", TaskName)) { return false; }
        if (CodexFollowWatcher.WaitForRunning(expected: true, TimeSpan.FromSeconds(10))) { return true; }
        AppLog.Warn("Codex follow: the scheduled watcher did not start.");
        return false;
    }

    public static bool Unregister()
    {
        // A missing task is already disabled. Avoid treating it as a failure.
        if (Run("/Query", "/TN", TaskName) && !Run("/Delete", "/TN", TaskName, "/F")) { return false; }
        if (CodexFollowWatcher.WaitForRunning(expected: false, TimeSpan.FromSeconds(5))) { return true; }
        AppLog.Warn("Codex follow: the watcher did not stop after being disabled.");
        return false;
    }

    private static bool Run(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
            using var process = Process.Start(info);
            if (process is null) { return false; }
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                AppLog.Warn("Codex follow: Task Scheduler command timed out.");
                return false;
            }

            if (process.ExitCode == 0) { return true; }
            AppLog.Warn($"Codex follow: Task Scheduler command failed ({process.ExitCode}): {error.Result} {output.Result}");
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Codex follow: could not access Task Scheduler.", ex);
            return false;
        }
    }
}
