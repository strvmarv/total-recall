using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class CloneSidelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-sideline-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 30, 0, TimeSpan.Zero);

    public CloneSidelineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            GitTestEnvironment.ForceDelete(_root);
        }
        catch (IOException)
        {
        }
    }

    private string Make(string name, string file = "content.txt")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(dir, file), "precious");
        return dir;
    }

    [Fact]
    public void MovesTheDirectoryWithItsContent_AndNamesItByLabelAndUtcTime()
    {
        var dir = Make("repo");
        var moved = CloneSideline.Move(dir, "unpushed", Now);

        Assert.Equal(dir + ".unpushed-20261116103000", moved);
        Assert.False(Directory.Exists(dir));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(moved, "content.txt")));
        Assert.True(File.Exists(Path.Combine(moved, ".git", "HEAD")));
    }

    [Fact]
    public void TheTimeIsConvertedToUtc()
    {
        var dir = Make("repo");
        var moved = CloneSideline.Move(dir, "left", new DateTimeOffset(2026, 11, 16, 12, 30, 0, TimeSpan.FromHours(2)));
        Assert.EndsWith("repo.left-20261116103000", moved);
    }

    [Fact]
    public void ATakenName_GetsASuffix()
    {
        var first = CloneSideline.Move(Make("repo"), "broken", Now);
        var second = CloneSideline.Move(Make("repo", "other.txt"), "broken", Now);
        var third = CloneSideline.Move(Make("repo", "third.txt"), "broken", Now);

        Assert.EndsWith("repo.broken-20261116103000", first);
        Assert.EndsWith("repo.broken-20261116103000-2", second);
        Assert.EndsWith("repo.broken-20261116103000-3", third);
        Assert.True(File.Exists(Path.Combine(first, "content.txt")));
        Assert.True(File.Exists(Path.Combine(second, "other.txt")));
    }

    [Fact]
    public void AFileWithTheTargetName_IsAvoidedToo()
    {
        var dir = Make("repo");
        File.WriteAllText(dir + ".left-20261116103000", "a file");
        var moved = CloneSideline.Move(dir, "left", Now);
        Assert.EndsWith("repo.left-20261116103000-2", moved);
        Assert.Equal("a file", File.ReadAllText(dir + ".left-20261116103000"));
    }

    [Fact]
    public void AMissingSource_Throws_AndNothingIsCreated()
    {
        var missing = Path.Combine(_root, "nope");
        Assert.Throws<IOException>(() => CloneSideline.Move(missing, "left", Now));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void ATrailingSeparatorIsTolerated()
    {
        var dir = Make("repo");
        var moved = CloneSideline.Move(dir + Path.DirectorySeparatorChar, "left", Now);
        Assert.EndsWith("repo.left-20261116103000", moved);
    }

    [Fact]
    public void TheRenameIsTheInjectedOne_AndAFailureLeavesTheSourceInPlace()
    {
        var dir = Make("repo");
        var ex = Assert.Throws<IOException>(() => CloneSideline.Move(dir, "left", Now, (_, _) => throw new IOException("denied")));
        Assert.Equal("denied", ex.Message);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "content.txt")));
    }

    [Fact]
    public void RejectsABlankDirectoryOrLabel()
    {
        Assert.Throws<ArgumentException>(() => CloneSideline.Move(" ", "left", Now));
        Assert.Throws<ArgumentException>(() => CloneSideline.Move(_root, "", Now));
    }
}
