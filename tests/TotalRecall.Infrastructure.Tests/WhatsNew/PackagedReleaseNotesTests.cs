using System;
using System.IO;
using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class PackagedReleaseNotesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tr-whatsnew-" + Guid.NewGuid().ToString("N"));

    public PackagedReleaseNotesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Put(string content)
    {
        var path = Path.Combine(_dir, PackagedReleaseNotes.FileName);
        File.WriteAllText(path, content);
        return path;
    }

    private const string RealShape =
        "{\"version\":\"4.8.0\",\"previous\":\"4.7.0\",\"summary\":\"Adds the what's-new notice.\"," +
        "\"sections\":{\"added\":[\"a\"],\"changed\":[],\"fixed\":[\"b\"]}," +
        "\"url\":\"https://github.com/strvmarv/total-recall/releases/tag/v4.8.0\"," +
        "\"releasesUrl\":\"https://github.com/strvmarv/total-recall/releases\"}";

    [Fact]
    public void FileName_IsReleaseNotesJson() =>
        Assert.Equal("release-notes.json", PackagedReleaseNotes.FileName);

    [Fact]
    public void Read_RealShapeFile_ReturnsVersionPreviousSummary()
    {
        Put(RealShape);
        var n = PackagedReleaseNotes.Read(_dir);
        Assert.NotNull(n);
        Assert.Equal("4.8.0", n!.Version);
        Assert.Equal("4.7.0", n.Previous);
        Assert.Equal("Adds the what's-new notice.", n.Summary);
    }

    [Fact]
    public void Read_FirstRelease_PreviousNull()
    {
        Put("{\"version\":\"1.2.0\",\"previous\":null,\"summary\":\"First.\"}");
        var n = PackagedReleaseNotes.Read(_dir);
        Assert.NotNull(n);
        Assert.Null(n!.Previous);
        Assert.Equal("First.", n.Summary);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull() =>
        Assert.Null(PackagedReleaseNotes.Read(_dir));

    [Fact]
    public void Read_MissingDirectory_ReturnsNull() =>
        Assert.Null(PackagedReleaseNotes.Read(Path.Combine(_dir, "nope")));

    [Fact]
    public void Read_DirectoryInsteadOfFile_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_dir, PackagedReleaseNotes.FileName));
        Assert.Null(PackagedReleaseNotes.Read(_dir));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"4.8.0\"")]
    [InlineData("{\"previous\":\"4.7.0\",\"summary\":\"x\"}")]
    [InlineData("{\"version\":4,\"summary\":\"x\"}")]
    [InlineData("{\"version\":null}")]
    public void Read_UnusableContent_ReturnsNull(string content)
    {
        var path = Put(content);
        Assert.Null(PackagedReleaseNotes.Read(_dir));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Read_BinaryContent_ReturnsNull()
    {
        var path = Path.Combine(_dir, PackagedReleaseNotes.FileName);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x80, 0x81, 0x00 });
        Assert.Null(PackagedReleaseNotes.Read(_dir));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Read_HugeFile_ReturnsNullAndKeepsFile()
    {
        var path = Put("{\"version\":\"4.8.0\",\"summary\":\"" + new string('a', 2 * 1024 * 1024) + "\"}");
        Assert.Null(PackagedReleaseNotes.Read(_dir));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("{\"version\":\"4.8.0\",\"previous\":\"4.7.0\",\"summary\":42}")]
    [InlineData("{\"version\":\"4.8.0\",\"previous\":\"4.7.0\",\"summary\":null}")]
    [InlineData("{\"version\":\"4.8.0\",\"previous\":\"4.7.0\",\"summary\":[\"a\"]}")]
    [InlineData("{\"version\":\"4.8.0\",\"previous\":\"4.7.0\"}")]
    public void Read_BadSummary_BecomesEmptyAndKeepsOtherMembers(string content)
    {
        Put(content);
        var n = PackagedReleaseNotes.Read(_dir);
        Assert.NotNull(n);
        Assert.Equal("4.8.0", n!.Version);
        Assert.Equal("4.7.0", n.Previous);
        Assert.Equal("", n.Summary);
    }

    [Theory]
    [InlineData("{\"version\":\"4.8.0\",\"summary\":\"s\",\"previous\":7}")]
    [InlineData("{\"version\":\"4.8.0\",\"summary\":\"s\"}")]
    public void Read_BadOrMissingPrevious_BecomesNull(string content)
    {
        Put(content);
        var n = PackagedReleaseNotes.Read(_dir);
        Assert.NotNull(n);
        Assert.Null(n!.Previous);
        Assert.Equal("s", n.Summary);
    }

    [Fact]
    public void Read_VersionWithV_IsReturnedAsWritten()
    {
        Put("{\"version\":\"v4.8.0\",\"summary\":\"s\"}");
        Assert.Equal("v4.8.0", PackagedReleaseNotes.Read(_dir)!.Version);
    }

    [Fact]
    public void Read_NullDirectory_DoesNotThrow() =>
        Assert.Null(PackagedReleaseNotes.Read(null!));
}
