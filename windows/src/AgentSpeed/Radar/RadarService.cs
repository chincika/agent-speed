using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Radar;
using Microsoft.Win32;

namespace AgentSpeed.Radar;

/// <summary>One shared schedule for both surfaces; persists attempts across launches and retries every three hours.</summary>
internal sealed class RadarService : IDisposable
{
    private readonly RadarStore _store;
    private readonly HttpClientTransport _transport = new();
    private readonly RadarClient _client;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _round;
    private bool _disposed;
    private bool _started;

    public RadarService(Dispatcher dispatcher, bool networkEnabled)
    {
        _dispatcher = dispatcher;
        var custom = Environment.GetEnvironmentVariable("AGENT_SPEED_RADAR_DIR");
        var directory = custom is { Length: > 0 } && Path.IsPathFullyQualified(custom) && !custom.StartsWith(@"\\", StringComparison.Ordinal)
            ? custom : Path.Combine(Cost.CostService.AppDataDirectory, "radar");
        _store = new RadarStore(directory);
        Cache = _store.Load();
        Failed = Cache.LastAttempt is { } attempt && (Cache.LastSuccess is null || Cache.LastSuccess < attempt);
        NetworkEnabled = networkEnabled;
        _client = new RadarClient(_transport, HttpFetchRequest.UserAgentFor(AppPaths.Version));
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => CheckDue(), dispatcher);
        _timer.Stop();
    }

    public event EventHandler? Changed;
    public RadarCache Cache { get; private set; }
    public RadarData? Data => Cache.Data;
    public bool IsChecking { get; private set; }
    public bool NetworkEnabled { get; private set; }
    public bool Failed { get; private set; }
    public bool LastChanged { get; private set; }
    public bool CanRefresh => NetworkEnabled && !IsChecking && !_disposed;

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _timer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        CheckDue();
    }

    public void SetNetworkEnabled(bool enabled)
    {
        NetworkEnabled = enabled;
        if (!enabled) _round?.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
        if (enabled && _started) CheckDue();
    }

    public void CheckDue()
    {
        if (CanRefresh && RadarStore.IsDue(DateTimeOffset.UtcNow, Cache.LastAttempt)) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        using var round = new CancellationTokenSource();
        _round = round;
        IsChecking = true;
        Changed?.Invoke(this, EventArgs.Empty);
        var previous = Cache;
        try
        {
            var result = await Task.Run(() => _client.FetchAsync(round.Token), round.Token);
            if (round.IsCancellationRequested || _disposed || !NetworkEnabled) return;
            Cache = RadarStore.Accept(previous, result, DateTimeOffset.UtcNow);
            LastChanged = !ReferenceEquals(Cache.Data, previous.Data);
            Failed = false;
            AppLog.Info($"Radar JSON sync: {(LastChanged ? "updated" : "unchanged")}, {Cache.Data?.Scores.Length} scores.");
            Save();
        }
        catch (OperationCanceledException) when (round.IsCancellationRequested) { }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException or HttpFetchException)
        {
            if (_disposed || round.IsCancellationRequested) return;
            Cache = previous with { LastAttempt = DateTimeOffset.UtcNow };
            Failed = true;
            LastChanged = false;
            Save();
            AppLog.Warn("Radar sync failed; keeping last good data.", ex);
        }
        finally
        {
            _round = null;
            IsChecking = false;
            if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Save()
    {
        try { _store.Save(Cache); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Radar cache could not be saved.", ex); }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _dispatcher.BeginInvoke(() => { if (!_disposed) CheckDue(); });
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        if (_started) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _round?.Cancel();
        _transport.Dispose();
    }
}
