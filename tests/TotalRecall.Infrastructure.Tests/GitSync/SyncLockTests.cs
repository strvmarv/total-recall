using System.Diagnostics;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>A fact that needs Linux and <c>flock(1)</c>.</summary>
internal sealed class FlockFactAttribute : FactAttribute
{
    public FlockFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "needs Linux";
        else if (GitExecutable.TryFindProgram("flock") is null)
            Skip = "flock is not on the PATH";
    }
}

[Collection("process-environment")]
public sealed class SyncLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-lock-" + Guid.NewGuid().ToString("N"));

    private string LockPath(string name = "lock") => Path.Combine(_root, "nested", "sync", name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Posix.Mode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task FreeLock_IsAcquired_AndCreatesTheDirectory()
    {
        var path = LockPath();
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        using var handle = await SyncLock.TryAcquireAsync(path, TimeSpan.Zero);
        Assert.NotNull(handle);
        Assert.Equal(Path.GetFullPath(path), handle!.Path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Dispose_ReleasesTheLock_AndIsIdempotent()
    {
        var first = await SyncLock.TryAcquireAsync(LockPath(), TimeSpan.Zero);
        Assert.NotNull(first);
        first!.Dispose();
        first.Dispose();
        using var second = await SyncLock.TryAcquireAsync(LockPath(), TimeSpan.Zero);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task SecondTaker_InTheSameProcess_WaitsThenReportsBusy()
    {
        using var holder = await SyncLock.TryAcquireAsync(LockPath(), TimeSpan.Zero);
        Assert.NotNull(holder);

        var watch = Stopwatch.StartNew();
        var busy = await SyncLock.TryAcquireAsync(LockPath(), TimeSpan.FromMilliseconds(300));
        Assert.Null(busy);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));

        var zero = await SyncLock.TryAcquireAsync(LockPath(), TimeSpan.Zero);
        Assert.Null(zero);

        // a waiter acquires soon after the holder lets go (it polls every 50 ms)
        var waiter = SyncLock.TryAcquireAsync(LockPath(), TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        var released = Stopwatch.StartNew();
        holder!.Dispose();
        using var acquired = await waiter;
        Assert.NotNull(acquired);
        Assert.True(released.Elapsed < TimeSpan.FromMilliseconds(500), $"took {released.Elapsed}");
    }

    [Fact]
    public async Task CancelledWait_ReturnsPromptly_AsCancellation()
    {
        using var holder = await SyncLock.TryAcquireAsync(LockPath(), TimeSpan.Zero);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SyncLock.TryAcquireAsync(LockPath(), TimeSpan.FromSeconds(10), cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");

        using var already = new CancellationTokenSource();
        already.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SyncLock.TryAcquireAsync(LockPath("other"), TimeSpan.Zero, already.Token));
    }

    [Fact]
    public async Task IndependentPaths_DoNotInteract_AndTheFileIsNeverDeleted()
    {
        using var a = await SyncLock.TryAcquireAsync(LockPath("a"), TimeSpan.Zero);
        using var b = await SyncLock.TryAcquireAsync(LockPath("b"), TimeSpan.Zero);
        Assert.NotNull(a);
        Assert.NotNull(b);
        a!.Dispose();
        Assert.True(File.Exists(LockPath("a")));
        Assert.Equal(0, new FileInfo(LockPath("a")).Length); // no diagnostic text is written
    }

    [FlockFact]
    public async Task HolderKilled_FreesTheLock()
    {
        var path = LockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        var info = new ProcessStartInfo(GitExecutable.TryFindProgram("flock")!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { path, "sh", "-c", "echo ready; sleep 60" })
            info.ArgumentList.Add(arg);
        using var child = Process.Start(info)!;
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ready", ready);

            Assert.Null(await SyncLock.TryAcquireAsync(path, TimeSpan.FromMilliseconds(200)));

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var watch = Stopwatch.StartNew();
            using var handle = await SyncLock.TryAcquireAsync(path, TimeSpan.FromSeconds(2));
            Assert.NotNull(handle);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task AnIoErrorThatIsNotBusy_Propagates()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath())!);
        var tooLong = Path.Combine(Path.GetDirectoryName(LockPath())!, new string('x', 300));
        var thrown = await Record.ExceptionAsync(() => SyncLock.TryAcquireAsync(tooLong, TimeSpan.Zero));
        Assert.NotNull(thrown);
        Assert.IsAssignableFrom<IOException>(thrown);
        Assert.False(SyncLock.IsSharingViolation(((IOException)thrown!).HResult, OperatingSystem.IsWindows()));
    }

    [Fact]
    public async Task ANegativeWait_IsOneAttempt()
    {
        using var holder = await SyncLock.TryAcquireAsync(LockPath(), TimeSpan.Zero);
        Assert.Null(await SyncLock.TryAcquireAsync(LockPath(), TimeSpan.MinValue));
        Assert.Null(await SyncLock.TryAcquireAsync(LockPath(), Timeout.InfiniteTimeSpan));
    }

    [ShFact]
    public async Task PermissionError_Throws_NotBusy()
    {
        var directory = Path.Combine(_root, "readonly");
        Directory.CreateDirectory(directory);
        Posix.Mode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                using var probe = File.Create(Path.Combine(directory, "probe"));
                return; // running as root (or the file system ignores modes): the case cannot arise, so this passes vacuously there
            }
            catch (UnauthorizedAccessException)
            {
            }
            var thrown = await Record.ExceptionAsync(() => SyncLock.TryAcquireAsync(Path.Combine(directory, "lock"), TimeSpan.Zero));
            Assert.NotNull(thrown);
            Assert.True(thrown is UnauthorizedAccessException || (thrown is IOException io && !SyncLock.IsSharingViolation(io.HResult, false)), thrown!.ToString());
        }
        finally
        {
            Posix.Mode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task FileLockingDisabled_Throws()
    {
        var saved = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        try
        {
            foreach (var value in new[] { "1", "true", "TRUE" })
            {
                Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", value);
                await Assert.ThrowsAsync<NotSupportedException>(() => SyncLock.TryAcquireAsync(LockPath(), TimeSpan.Zero));
            }
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", "0");
            using var fine = await SyncLock.TryAcquireAsync(LockPath(), TimeSpan.Zero);
            Assert.NotNull(fine);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", saved);
        }

        try
        {
            AppContext.SetSwitch("System.IO.DisableFileLocking", true);
            await Assert.ThrowsAsync<NotSupportedException>(() => SyncLock.TryAcquireAsync(LockPath("switch"), TimeSpan.Zero));
        }
        finally
        {
            AppContext.SetSwitch("System.IO.DisableFileLocking", false);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true, true)]
    [InlineData(unchecked((int)0x80070021), true, true)]
    [InlineData(unchecked((int)0x80070005), true, false)]
    [InlineData(2, true, false)]
    [InlineData(32, true, false)] // a bare 32 is not an HRESULT of the Win32 facility
    [InlineData(11, false, true)]
    [InlineData(35, false, true)]
    [InlineData(13, false, false)]
    [InlineData(2, false, false)]
    [InlineData(unchecked((int)0x80070005), false, false)]
    [InlineData(unchecked((int)0x80070020), false, false)]
    public void BusyDiscriminator(int hresult, bool windows, bool expected) =>
        Assert.Equal(expected, SyncLock.IsSharingViolation(hresult, windows));
}
