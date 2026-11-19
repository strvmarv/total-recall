using System;
using System.IO;
using TotalRecall.Infrastructure.Config;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Config;

/// <summary>
/// The [transcripts] settings of the opt-in archive: off unless enabled is the
/// boolean true; any read problem gives the defaults; a bad db_path is reported
/// in DbPathError and leaves DbPath empty.
/// </summary>
[Collection("process-environment")]
public sealed class ConfigLoaderTranscriptsTests : IDisposable
{
    private readonly string _dir;

    public ConfigLoaderTranscriptsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tr-transcripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string toml)
    {
        var cfg = Path.Combine(_dir, "config.toml");
        File.WriteAllText(cfg, toml);
        return cfg;
    }

    private static readonly TranscriptsSettings Defaults = new(false, "", null, 0, false);

    [Fact]
    public void NoUserFile_IsDefaults()
    {
        Assert.Equal(Defaults, ConfigLoader.LoadTranscriptsSettings(Path.Combine(_dir, "absent.toml")));
    }

    [Fact]
    public void EnabledTrue_ReadsTrue()
    {
        var s = ConfigLoader.LoadTranscriptsSettings(Write("[transcripts]\nenabled = true\n"));
        Assert.True(s.Enabled);
        Assert.Equal("", s.DbPath);
        Assert.Null(s.DbPathError);
    }

    [Fact]
    public void EnabledWrongType_ReadsFalse()
    {
        Assert.False(ConfigLoader.LoadTranscriptsSettings(Write("[transcripts]\nenabled = \"yes\"\n")).Enabled);
    }

    [Fact]
    public void BrokenToml_IsDefaults()
    {
        Assert.Equal(Defaults, ConfigLoader.LoadTranscriptsSettings(Write("[transcripts\nenabled = = true\n")));
    }

    [Fact]
    public void TranscriptsNotATable_IsDefaults()
    {
        Assert.Equal(Defaults, ConfigLoader.LoadTranscriptsSettings(Write("transcripts = true\n")));
    }

    [Fact]
    public void UnreadableUserPath_IsDefaults()
    {
        Assert.Equal(Defaults, ConfigLoader.LoadTranscriptsSettings(_dir));
    }

    [Fact]
    public void AbsoluteDbPath_IsReturned()
    {
        var path = Path.Combine(Path.GetTempPath(), "x.db");
        var s = ConfigLoader.LoadTranscriptsSettings(Write($"[transcripts]\ndb_path = '{path}'\n"));
        Assert.Equal(path, s.DbPath);
        Assert.Null(s.DbPathError);
    }

    [Fact]
    public void LiteralAbsoluteDbPath_Tmp()
    {
        if (OperatingSystem.IsWindows()) return;
        var s = ConfigLoader.LoadTranscriptsSettings(Write("[transcripts]\ndb_path = \"/tmp/x.db\"\n"));
        Assert.Equal("/tmp/x.db", s.DbPath);
        Assert.Null(s.DbPathError);
    }

    [Theory]
    [InlineData("C:archive.db")]
    [InlineData("\\archive.db")]
    public void RootedButNotFullyQualifiedDbPath_IsRejected_OnWindows(string value)
    {
        // Rooted but relative to the current directory (drive-relative or root-relative) only exists on Windows.
        if (!OperatingSystem.IsWindows()) return;
        var s = ConfigLoader.LoadTranscriptsSettings(Write($"[transcripts]\ndb_path = '{value}'\n"));
        Assert.NotNull(s.DbPathError);
        Assert.Contains("transcripts.db_path must be an absolute path or start with ~/", s.DbPathError);
        Assert.Equal("", s.DbPath);
    }

    [Fact]
    public void TildeDbPath_IsExpanded()
    {
        var old = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", _dir);
            var s = ConfigLoader.LoadTranscriptsSettings(Write("[transcripts]\ndb_path = \"~/a/t.db\"\n"));
            Assert.Null(s.DbPathError);
            Assert.Equal(Path.Combine(_dir, "a", "t.db"), s.DbPath);
        }
        finally { Environment.SetEnvironmentVariable("HOME", old); }
    }

    [Fact]
    public void DbPathWrongType_SetsError()
    {
        var s = ConfigLoader.LoadTranscriptsSettings(Write("[transcripts]\ndb_path = 5\n"));
        Assert.NotNull(s.DbPathError);
        Assert.Equal("", s.DbPath);
    }

    [Theory]
    [InlineData("relative.db")]
    [InlineData("/tmp/x/")]
    [InlineData("~")]
    public void InvalidDbPath_SetsError(string value)
    {
        var s = ConfigLoader.LoadTranscriptsSettings(Write($"[transcripts]\ndb_path = '{value}'\n"));
        Assert.NotNull(s.DbPathError);
        Assert.Equal("", s.DbPath);
    }

    [Fact]
    public void BlankDbPath_MeansMainDatabase()
    {
        var s = ConfigLoader.LoadTranscriptsSettings(Write("[transcripts]\ndb_path = \"  \"\n"));
        Assert.Equal("", s.DbPath);
        Assert.Null(s.DbPathError);
    }

    [Fact]
    public void NegativeRetention_ReadsZero()
    {
        Assert.Equal(0, ConfigLoader.LoadTranscriptsSettings(Write("[transcripts]\nretention_days = -3\n")).RetentionDays);
    }

    [Fact]
    public void RetentionDays_ReadsPositive()
    {
        Assert.Equal(30, ConfigLoader.LoadTranscriptsSettings(Write("[transcripts]\nretention_days = 30\n")).RetentionDays);
    }

    [Fact]
    public void AllowRemoteEmbedding_ReadsTrue()
    {
        Assert.True(ConfigLoader.LoadTranscriptsSettings(Write("[transcripts]\nallow_remote_embedding = true\n")).AllowRemoteEmbedding);
    }
}
