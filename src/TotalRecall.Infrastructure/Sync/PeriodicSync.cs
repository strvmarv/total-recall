using System;
using System.Threading;
using System.Threading.Tasks;

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
        _timer = new Timer(OnTick, null, TimeSpan.Zero, interval);
    }

    private async void OnTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_gate.Wait(0)) return;

        try
        {
            await OnTickAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                try { _gate.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>
    /// Runs one sync tick: pull memories, pull skills, flush queue.
    /// Each phase is isolated so a failure in one does not prevent the others.
    /// Internal for test access via InternalsVisibleTo.
    /// </summary>
    internal async Task OnTickAsync(CancellationToken ct)
    {
        try { await _syncService.PullAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"[total-recall] PullAsync failed: {ex.Message}"); }

        try { await _syncService.PullSkillsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"[total-recall] PullSkillsAsync failed: {ex.Message}"); }

        try { await _syncService.FlushAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"[total-recall] FlushAsync failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        _timer = null;
        _gate.Dispose();
    }
}
