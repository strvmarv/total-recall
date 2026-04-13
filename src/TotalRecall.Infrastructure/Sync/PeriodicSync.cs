using System;
using System.Threading;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// Background timer that periodically runs bidirectional sync (pull + flush)
/// in cortex mode. Created only when sync_interval_seconds > 0.
/// </summary>
public sealed class PeriodicSync : IDisposable
{
    private readonly SyncService _syncService;
    private readonly int _intervalSeconds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private int _disposed;

    public PeriodicSync(SyncService syncService, int intervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(syncService);
        if (intervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "Must be > 0");
        _syncService = syncService;
        _intervalSeconds = intervalSeconds;
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_timer is not null) return;

        var interval = TimeSpan.FromSeconds(_intervalSeconds);
        _timer = new Timer(OnTick, null, interval, interval);
    }

    private async void OnTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_gate.Wait(0)) return;

        try
        {
            await _syncService.PullAsync(CancellationToken.None).ConfigureAwait(false);
            await _syncService.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[total-recall] periodic sync failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        _timer = null;
        _gate.Dispose();
    }
}
