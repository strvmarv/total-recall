using System;
using System.IO;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class PrivateFilesTests : IDisposable
{
    private readonly string _dir;

    public PrivateFilesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tr-private-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnsureDirectory_CreatesNestedDirectories_Private()
    {
        var path = Path.Combine(_dir, "a", "b");
        PrivateFiles.EnsureDirectory(path);
        Assert.True(Directory.Exists(path));
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(_dir, "a")));
    }

    [Fact]
    public void EnsureDirectory_ExistingDirectory_IsLeftAlone()
    {
        var path = Path.Combine(_dir, "exists");
        Directory.CreateDirectory(path);
        PrivateFiles.EnsureDirectory(path);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void CreateEmptyFile_CreatesPrivateFile()
    {
        var path = Path.Combine(_dir, "f");
        PrivateFiles.CreateEmptyFile(path);
        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void CreateEmptyFile_ExistingFile_KeepsContentAndTightensMode()
    {
        var path = Path.Combine(_dir, "f");
        File.WriteAllText(path, "keep");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        PrivateFiles.CreateEmptyFile(path);
        Assert.Equal("keep", File.ReadAllText(path));
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
