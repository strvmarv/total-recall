using System;
using System.IO;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class TranscriptSentinelTests : IDisposable
{
    private readonly string _dir;

    public TranscriptSentinelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tr-sentinel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Core.Config.TotalRecallConfig Load(string toml)
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(path, toml);
        return new ConfigLoader().LoadEffectiveConfig(path);
    }

    [Fact]
    public void Path_IsLiveEnabledUnderDataDir()
    {
        Assert.Equal(System.IO.Path.Combine(_dir, "live", "enabled"), new TranscriptSentinel(_dir).Path);
    }

    [Fact]
    public void IsEnabled_FalseOnEmptyDataDir_TrueAfterWrite()
    {
        var s = new TranscriptSentinel(_dir);
        Assert.False(s.IsEnabled());
        s.Write();
        Assert.True(s.IsEnabled());
    }

    [Fact]
    public void Write_IsIdempotent()
    {
        var s = new TranscriptSentinel(_dir);
        s.Write();
        s.Write();
        Assert.True(s.IsEnabled());
    }

    [Fact]
    public void Remove_OnMissingFile_DoesNotThrow()
    {
        var s = new TranscriptSentinel(_dir);
        s.Remove();
        Assert.False(s.IsEnabled());
    }

    [Fact]
    public void Remove_DeletesFile()
    {
        var s = new TranscriptSentinel(_dir);
        s.Write();
        s.Remove();
        Assert.False(s.IsEnabled());
    }

    [Fact]
    public void Write_CreatesPrivateDirectoryAndFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var s = new TranscriptSentinel(_dir);
        s.Write();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(System.IO.Path.Combine(_dir, "live")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(s.Path));
    }

    [Fact]
    public void EffectiveStorageMode_ConnectionStringOnly_IsPostgres()
    {
        var cfg = Load("[storage]\nconnection_string = \"Host=x\"\n");
        Assert.Equal("postgres", TranscriptSentinel.EffectiveStorageMode(cfg));
    }

    [Fact]
    public void EffectiveStorageMode_ModeWinsOverConnectionString()
    {
        var cfg = Load("[storage]\nmode = \"git\"\nconnection_string = \"Host=x\"\n");
        Assert.Equal("git", TranscriptSentinel.EffectiveStorageMode(cfg));
    }

    [Fact]
    public void EffectiveStorageMode_NoStorage_IsLocal()
    {
        var cfg = new ConfigLoader().LoadDefaults();
        Assert.Equal("local", TranscriptSentinel.EffectiveStorageMode(cfg));
    }

    [Theory]
    [InlineData(true, "postgres", false)]
    [InlineData(true, "POSTGRES", false)]
    [InlineData(true, "sqlite", true)]
    [InlineData(true, "cortex", true)]
    [InlineData(true, "git", true)]
    [InlineData(false, "sqlite", false)]
    [InlineData(false, "postgres", false)]
    public void IsActive_Matrix(bool enabled, string mode, bool expected)
    {
        Assert.Equal(expected, TranscriptSentinel.IsActive(enabled, mode));
    }

    [Fact]
    public void Mirror_WritesWhenActive_RemovesWhenNot()
    {
        var s = new TranscriptSentinel(_dir);
        s.Mirror(true, "sqlite");
        Assert.True(s.IsEnabled());
        s.Mirror(true, "postgres");
        Assert.False(s.IsEnabled());
        s.Mirror(true, "git");
        Assert.True(s.IsEnabled());
        s.Mirror(false, "git");
        Assert.False(s.IsEnabled());
    }

    [Fact]
    public void Mirror_UnwritableDataDir_NeitherThrowsNorLeavesFile()
    {
        var blocker = System.IO.Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var s = new TranscriptSentinel(System.IO.Path.Combine(blocker, "data"));
        s.Mirror(true, "sqlite");
        Assert.False(s.IsEnabled());
        Assert.False(File.Exists(s.Path));
        s.Mirror(false, "sqlite");
    }
}
