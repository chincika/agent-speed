using AgentSpeed.App;

namespace AgentSpeed.Quota;

/// <summary>One immediate read when enabled, then one read every three minutes. No work while disabled.</summary>
internal sealed class CodexQuotaService(
    Action changed,
    Func<CancellationToken, Task<CodexQuotaSnapshot>>? read = null,
    TimeSpan? refreshInterval = null) : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(3);
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<CodexQuotaSnapshot>> _read = read ?? CodexQuotaClient.ReadAsync;
    private CancellationTokenSource? _cancellation;
    private Task _running = Task.CompletedTask;
    private CodexQuotaSnapshot _snapshot = CodexQuotaSnapshot.Unknown;
    private bool _disposed;

    public CodexQuotaSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || enabled == (_cancellation is not null)) return;
            _cancellation?.Cancel();
            _cancellation = null;
            Volatile.Write(ref _snapshot, CodexQuotaSnapshot.Unknown);
            if (enabled)
            {
                var cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                var previous = _running;
                _running = Task.Run(() => RunAsync(previous, cancellation));
            }
        }
    }

    private async Task RunAsync(Task previous, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            // Fast off/on sequences must finish cancellation and clean up the previous child first.
            await previous;
            using var timer = new PeriodicTimer(refreshInterval ?? RefreshInterval);
            do
            {
                token.ThrowIfCancellationRequested();
                CodexQuotaSnapshot next;
                try { next = await _read(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Never print RPC data, credentials or account identifiers in application logs.
                    AppLog.Info($"Codex quota: read unavailable ({ex.GetType().Name}); retrying in three minutes.");
                    next = CodexQuotaSnapshot.Unknown;
                }
                lock (_gate)
                {
                    if (token.IsCancellationRequested) break;
                    Volatile.Write(ref _snapshot, next);
                }
                AppLog.Info($"Codex quota: 5h remaining {CodexQuotaSnapshot.Percent(next.FiveHourRemaining)}, week remaining {CodexQuotaSnapshot.Percent(next.WeekRemaining)}.");
                changed();
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { cancellation.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _cancellation?.Cancel();
            _cancellation = null;
        }
    }
}
