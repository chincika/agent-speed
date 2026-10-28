using System.Runtime.InteropServices;
using System.Windows.Threading;
using AgentSpeed.Interop;
using Microsoft.Win32.SafeHandles;

namespace AgentSpeed.App;

/// <summary>Read only the foreground window's executable, never its title or any window contents.</summary>
internal static unsafe partial class CodexForeground
{
    public static bool Read() => IsCodexWindow(User32.GetForegroundWindow());

    internal static bool IsCodexWindow(nint window)
    {
        if (window == 0 || !User32.IsWindowVisible(window)) return false;
        User32.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return false;
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return false;
        var buffer = stackalloc char[32768];
        var size = 32768u;
        return QueryFullProcessImageName(process, 0, buffer, ref size) && size is > 0 and < 32768
            && CodexDesktop.IsDesktopExecutable(new string(buffer, 0, (int)size));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* name, ref uint size);
}

/// <summary>Enabled only for the foreground setting; changes presentation without changing saved orb visibility.</summary>
internal sealed class CodexForegroundMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action<bool> _apply;
    private readonly Func<bool> _read;
    private bool _enabled;
    private bool? _lastVisible;
    private bool _disposed;

    public CodexForegroundMonitor(Dispatcher dispatcher, Action<bool> apply, Func<bool>? read = null)
    {
        _apply = apply;
        _read = read ?? CodexForeground.Read;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
        _timer.Stop();
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed) return;
        _enabled = enabled;
        _timer.IsEnabled = enabled;
        Refresh();
    }

    internal void Refresh()
    {
        if (_disposed) return;
        var visible = !_enabled || _read();
        if (visible == _lastVisible) return;
        _lastVisible = visible;
        _apply(visible);
    }

    public void Dispose() { _disposed = true; _timer.Stop(); }
}
