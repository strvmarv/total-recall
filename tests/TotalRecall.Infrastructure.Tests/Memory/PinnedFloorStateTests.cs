using System;
using System.IO;
using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Memory;

public sealed class PinnedFloorStateTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tr-floor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void Load_MissingFile_ReturnsFreshUnseeded()
    {
        var s = PinnedFloorState.Load(_dir, "sess-1");
        Assert.Equal("sess-1", s.SessionId);
        Assert.Equal(0, s.TurnCount);
        Assert.False(s.Seeded);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var saved = new FloorState("sess-2", 7, 4, 12345L, true);
        PinnedFloorState.Save(_dir, saved);
        var loaded = PinnedFloorState.Load(_dir, "sess-2");
        Assert.Equal(saved, loaded);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsFreshUnseeded()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, PinnedFloorState.FileName("sess-3")), "{ not json");
        var s = PinnedFloorState.Load(_dir, "sess-3");
        Assert.False(s.Seeded);
        Assert.Equal(0, s.TurnCount);
    }

    [Fact]
    public void FileName_SanitizesUnsafeChars()
    {
        var name = PinnedFloorState.FileName("a/b\\c:d");
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.EndsWith(".json", name);
    }

    [Fact]
    public void Prune_DeletesOldFilesOnly()
    {
        PinnedFloorState.Save(_dir, new FloorState("old", 1, 1, 0, true));
        PinnedFloorState.Save(_dir, new FloorState("new", 1, 1, 0, true));
        var oldPath = Path.Combine(_dir, PinnedFloorState.FileName("old"));
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-30));

        PinnedFloorState.Prune(_dir, maxAgeDays: 7, nowUtc: DateTimeOffset.UtcNow);

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(Path.Combine(_dir, PinnedFloorState.FileName("new"))));
    }

    [Fact]
    public void Save_OverwritesExistingState()
    {
        PinnedFloorState.Save(_dir, new FloorState("s", 1, 1, 100, true));
        PinnedFloorState.Save(_dir, new FloorState("s", 9, 8, 7000, true));
        var loaded = PinnedFloorState.Load(_dir, "s");
        Assert.Equal(9, loaded.TurnCount);
        Assert.Equal(8, loaded.LastInjectedTurn);
        Assert.Equal(7000, loaded.LastInjectedBytes);
    }

    [Fact]
    public void Prune_WhenDirAbsent_DoesNotThrow()
    {
        var missing = System.IO.Path.Combine(_dir, "does-not-exist");
        var ex = Record.Exception(() =>
            PinnedFloorState.Prune(missing, maxAgeDays: 7, nowUtc: DateTimeOffset.UtcNow));
        Assert.Null(ex);
    }

    [Fact]
    public void RoundTrip_LargeLongBytes()
    {
        var saved = new FloorState("big", 2, 1, long.MaxValue, true);
        PinnedFloorState.Save(_dir, saved);
        Assert.Equal(long.MaxValue, PinnedFloorState.Load(_dir, "big").LastInjectedBytes);
    }

    [Fact]
    public void Prune_DeletesOldSentinels_KeepsFreshSentinels()
    {
        Directory.CreateDirectory(_dir);
        var oldSentinel = Path.Combine(_dir, "compaction-nudged-old-session");
        var freshSentinel = Path.Combine(_dir, "compaction-nudged-fresh-session");
        File.WriteAllText(oldSentinel, "1");
        File.WriteAllText(freshSentinel, "1");
        File.SetLastWriteTimeUtc(oldSentinel, DateTime.UtcNow.AddDays(-30));

        PinnedFloorState.Prune(_dir, maxAgeDays: 7, nowUtc: DateTimeOffset.UtcNow);

        Assert.False(File.Exists(oldSentinel));
        Assert.True(File.Exists(freshSentinel));
    }
}
