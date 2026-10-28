using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AgentSpeed.App;

namespace AgentSpeed.Quota;

/// <summary>Read-only RPC through the installed Codex executable, reusing its own login and transport.</summary>
internal static class CodexQuotaClient
{
    public static async Task<CodexQuotaSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var start = new ProcessStartInfo(FindExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the local Codex interface.");
        // Drain diagnostics without retaining them: they can contain private local paths or account details.
        var diagnostics = DrainAsync(process.StandardError, token);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"agent_speed_quota\",\"title\":\"Agent Speed\",\"version\":\"1.4.1\"}}}".AsMemory(), token);
            await ResponseAsync(process.StandardOutput, 1, token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), token);
            await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}".AsMemory(), token);
            var result = await ResponseAsync(process.StandardOutput, 2, token);
            return CodexQuotaSnapshot.Parse(result);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            await timeout.CancelAsync();
            await diagnostics;
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            var buffer = new char[1024];
            while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static async Task<JsonElement> ResponseAsync(StreamReader reader, int id, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line)
        {
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number
                || !responseId.TryGetInt32(out var value) || value != id) continue;
            if (root.TryGetProperty("error", out _)) throw new IOException("The local Codex quota interface is unavailable. Check Codex sign-in.");
            return root.GetProperty("result").Clone();
        }
        throw new IOException("The local Codex interface closed before replying.");
    }

    internal static string FindExecutable()
    {
        using var current = Process.GetCurrentProcess();
        // Prefer the exact CLI currently used by the local desktop, excluding its GUI executable.
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (process.SessionId == current.SessionId && path is not null
                        && !CodexDesktop.IsDesktopExecutable(path) && File.Exists(path)) return path;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }

        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(bin))
        {
            var installed = Directory.EnumerateDirectories(bin).Select(p => Path.Combine(p, "codex.exe"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (installed is not null) return installed;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(candidate) && !CodexDesktop.IsDesktopExecutable(candidate)) return candidate;
        }
        throw new FileNotFoundException("Install or start Codex to read its quota.");
    }
}
