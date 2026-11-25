using System;
using System.IO;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// The optional busy timeout of <see cref="SqliteConnection.Open"/>: the default stays 5000 ms; a shorter value is
/// applied through the pragma and also bounds the driver's own retry loop (<c>Default Timeout</c>, in whole seconds).
/// Not <c>Integration</c>-trait.
/// </summary>
public sealed class SqliteConnectionBusyTimeoutTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "tr-busy-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        MsSqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch { /* best effort */ }
        }
    }

    private static int Pragma(MsSqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public void Open_DefaultBusyTimeout_Is5000_AndTheDriverTimeoutIsUntouched()
    {
        using var plain = SqliteConnection.Open(_path, pooling: false);
        Assert.Equal(5000, Pragma(plain));
        using var raw = new MsSqliteConnection("Data Source=" + _path + ";Pooling=False");
        Assert.Equal(raw.DefaultTimeout, plain.DefaultTimeout);
    }

    [Fact]
    public void Open_BusyTimeout500_IsAppliedToThePragma_AndBoundsTheDriverTimeout()
    {
        using var conn = SqliteConnection.Open(_path, pooling: false, busyTimeoutMs: 500);
        Assert.Equal(500, Pragma(conn));
        Assert.Equal(1, conn.DefaultTimeout); // 500 ms rounded up to whole seconds
    }

    [Theory]
    [InlineData(1000, 1)]
    [InlineData(1500, 2)]
    [InlineData(1, 1)]
    public void Open_BusyTimeout_RoundsTheDriverTimeoutUp(int ms, int seconds)
    {
        using var conn = SqliteConnection.Open(_path, pooling: false, busyTimeoutMs: ms);
        Assert.Equal(ms, Pragma(conn));
        Assert.Equal(seconds, conn.DefaultTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Open_BusyTimeoutBelowOne_IsRefused(int ms) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SqliteConnection.Open(_path, pooling: false, busyTimeoutMs: ms));
}
