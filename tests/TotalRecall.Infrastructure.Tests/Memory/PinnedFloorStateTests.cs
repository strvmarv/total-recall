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
}
