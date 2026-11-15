using System;
using System.IO;
using TotalRecall.Infrastructure.Config;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Config;

/// <summary>
/// updates.notice switches the "what's new" notice off. It is on unless the
/// effective config holds the boolean false; any problem reads as on.
/// </summary>
public sealed class ConfigLoaderUpdatesTests : IDisposable
{
    private readonly string _dir;

    public ConfigLoaderUpdatesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tr-updates-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void LoadUpdatesNoticeEnabled_NoUserFile_IsTrue()
    {
        Assert.True(ConfigLoader.LoadUpdatesNoticeEnabled(Path.Combine(_dir, "absent.toml")));
    }

    [Fact]
    public void LoadUpdatesNoticeEnabled_NoticeFalse_IsFalse()
    {
        Assert.False(ConfigLoader.LoadUpdatesNoticeEnabled(Write("[updates]\nnotice = false\n")));
    }

    [Fact]
    public void LoadUpdatesNoticeEnabled_NoticeTrue_IsTrue()
    {
        Assert.True(ConfigLoader.LoadUpdatesNoticeEnabled(Write("[updates]\nnotice = true\n")));
    }

    [Fact]
    public void LoadUpdatesNoticeEnabled_TableAbsentInUserFile_IsTrue()
    {
        Assert.True(ConfigLoader.LoadUpdatesNoticeEnabled(Write("[tiers.hot]\nmax_entries = 50\n")));
    }

    [Fact]
    public void LoadUpdatesNoticeEnabled_WrongType_IsTrue()
    {
        Assert.True(ConfigLoader.LoadUpdatesNoticeEnabled(Write("[updates]\nnotice = \"no\"\n")));
    }

    [Fact]
    public void LoadUpdatesNoticeEnabled_UpdatesNotATable_IsTrue()
    {
        Assert.True(ConfigLoader.LoadUpdatesNoticeEnabled(Write("updates = false\n")));
    }

    [Fact]
    public void LoadUpdatesNoticeEnabled_BrokenToml_IsTrue()
    {
        Assert.True(ConfigLoader.LoadUpdatesNoticeEnabled(Write("[updates\nnotice = = false\n")));
    }

    [Fact]
    public void LoadUpdatesNoticeEnabled_UnreadableUserPath_IsTrue()
    {
        // A directory where the file should be: File.Exists is false, so the
        // defaults apply; a path that exists but cannot be read must not throw.
        var asDir = Path.Combine(_dir, "config.toml");
        Directory.CreateDirectory(asDir);
        Assert.True(ConfigLoader.LoadUpdatesNoticeEnabled(asDir));
    }

    [Fact]
    public void Defaults_StillLoad_AndSerializedSnapshotHasNoUpdatesKey()
    {
        var cfg = new ConfigLoader().LoadDefaults();
        var json = ConfigJsonSerializer.Serialize(cfg);
        Assert.DoesNotContain("updates", json, StringComparison.OrdinalIgnoreCase);
    }
}
