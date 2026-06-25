using System.IO;
using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Memory;

public sealed class CompactionNudgeTests
{
    [Fact]
    public void TryTake_ReturnsNudgeOnce_ThenNullSameSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tr-nudge-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var first = CompactionNudge.TryTake(dir, "sess-1", hotCount: 6, threshold: 5);
            var second = CompactionNudge.TryTake(dir, "sess-1", hotCount: 6, threshold: 5);

            Assert.NotNull(first);
            Assert.Contains("compact", first!);
            Assert.Null(second);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TryTake_ReturnsNull_BelowThreshold()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tr-nudge-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(CompactionNudge.TryTake(dir, "sess-2", hotCount: 2, threshold: 5));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
