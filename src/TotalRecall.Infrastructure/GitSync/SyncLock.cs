using System.Diagnostics;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// The operating-system lock that keeps two processes from syncing one database at once. It is a file opened with
/// <c>FileShare.None</c>, which the operating system releases when the holder dies, so a crash never leaves it held.
/// The file is never deleted and holds no text (a second taker cannot read it on Windows). No caller yet: the sync cycle that
/// takes it is milestone 1b. Two things for a caller: hold the handle in a <c>using</c> for the whole cycle (an undisposed
/// handle is released by the finalizer at some later collection); and .NET ignores a flock failure other than
/// "would block" (<c>ENOTSUP</c>, <c>ENOLCK</c>), so on a file system without flock support the lock excludes no one.
/// </summary>
public static class SyncLock
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Takes the lock, waiting up to <paramref name="wait"/> for a holder to let go (zero or less means one attempt).
    /// Returns null when it stayed busy.
    /// Cancelling the wait throws <see cref="OperationCanceledException"/> (cancellation is not "busy"); any failure that
    /// is not "someone holds it" (a missing permission, a file system error) throws, so busy is never confused with broken.
    /// </summary>
    public static async Task<SyncLockHandle?> TryAcquireAsync(string path, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureLockingWorks();
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        if (wait < TimeSpan.Zero)
            wait = TimeSpan.Zero;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
                return new SyncLockHandle(full, stream);
            }
            catch (IOException e) when (IsSharingViolation(e.HResult, OperatingSystem.IsWindows()))
            {
                // held by someone else: wait and try again
            }

            var remaining = wait - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return null;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when an open failure means "another process holds the file": a sharing or lock violation on Windows (the low
    /// 16 bits of the HRESULT are 32 or 33) and EAGAIN/EWOULDBLOCK on Unix (11 on Linux, 35 on macOS).
    /// </summary>
    internal static bool IsSharingViolation(int hresult, bool windows)
    {
        if (windows)
        {
            var code = hresult & 0xFFFF;
            return (hresult & 0x7FFF0000) == 0x00070000 && (code == 32 || code == 33);
        }
        return hresult is 11 or 35;
    }

    /// <summary>
    /// .NET turns its Unix file locking off when <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c> is 1 or true, or the
    /// <c>System.IO.DisableFileLocking</c> switch is set; the lock would then exclude no one, so it refuses to pretend.
    /// </summary>
    private static void EnsureLockingWorks()
    {
        var variable = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        var byVariable = string.Equals(variable, "1", StringComparison.Ordinal)
            || string.Equals(variable, "true", StringComparison.OrdinalIgnoreCase);
        var bySwitch = AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var enabled) && enabled;
        if (byVariable || bySwitch)
        {
            throw new NotSupportedException(
                "file locking is disabled (DOTNET_SYSTEM_IO_DISABLEFILELOCKING or System.IO.DisableFileLocking); sync cannot take its lock");
        }
    }
}

/// <summary>A held <see cref="SyncLock"/>; disposing it releases the lock. The file stays.</summary>
public sealed class SyncLockHandle : IDisposable
{
    private FileStream? _stream;

    internal SyncLockHandle(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
