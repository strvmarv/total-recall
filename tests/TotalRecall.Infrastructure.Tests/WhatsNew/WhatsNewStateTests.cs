using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class WhatsNewStateTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tr-whatsnew-" + Guid.NewGuid().ToString("N"));

    private string Dir => Path.Combine(_root, "whats-new");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Put(string name, string content)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, name), content);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultDir_EndsWithWhatsNew() =>
        Assert.Equal("whats-new", Path.GetFileName(WhatsNewState.DefaultDir()));

    [Theory]
    [InlineData("pi", "seen-pi.json", "direct-pi.json")]
    [InlineData("Claude-Code ", "seen-claude-code.json", "direct-claude-code.json")]
    [InlineData("../../etc/passwd", "seen-other.json", "direct-other.json")]
    [InlineData("a/b", "seen-other.json", "direct-other.json")]
    [InlineData("claude-code\n", "seen-other.json", "direct-other.json")]
    [InlineData("", "seen-other.json", "direct-other.json")]
    [InlineData(null, "seen-other.json", "direct-other.json")]
    public void FileNames_GoThroughNormalize(string? host, string seen, string stamp)
    {
        Assert.Equal(seen, WhatsNewState.SeenFileName(host!));
        Assert.Equal(stamp, WhatsNewState.StampFileName(host!));
    }

    [Fact]
    public void ReadSeenVersion_MissingFileOrDir_ReturnsNull()
    {
        Assert.Null(WhatsNewState.ReadSeenVersion(Dir, "pi"));
        Directory.CreateDirectory(Dir);
        Assert.Null(WhatsNewState.ReadSeenVersion(Dir, "pi"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":4}")]
    [InlineData("{\"version\":null}")]
    [InlineData("{\"version\":[\"4.8.0\"]}")]
    [InlineData("\"4.8.0\"")]
    public void ReadSeenVersion_CorruptOrWrongType_ReturnsNull(string content)
    {
        Put("seen-pi.json", content);
        Assert.Null(WhatsNewState.ReadSeenVersion(Dir, "pi"));
    }

    [Fact]
    public void ReadSeenVersion_DirectoryInsteadOfFile_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "seen-pi.json"));
        Assert.Null(WhatsNewState.ReadSeenVersion(Dir, "pi"));
    }

    [Fact]
    public void WriteSeenVersion_ThenRead_RoundTripsIntoNewDirectory()
    {
        Assert.False(Directory.Exists(Dir));
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.8.0-beta.2");
        Assert.Equal("4.8.0-beta.2", WhatsNewState.ReadSeenVersion(Dir, "pi"));
    }

    [Fact]
    public void WriteSeenVersion_FileIsJsonWithExactlyVersionKey()
    {
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.8.0");
        var text = File.ReadAllText(Path.Combine(Dir, "seen-pi.json"));
        using var doc = JsonDocument.Parse(text);
        var props = doc.RootElement.EnumerateObject().ToList();
        Assert.Single(props);
        Assert.Equal("version", props[0].Name);
        Assert.Equal("4.8.0", props[0].Value.GetString());
    }

    [Fact]
    public void WriteSeenVersion_Overwrites_AndLeavesNoTempFiles()
    {
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.7.0");
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.8.0");
        Assert.Equal("4.8.0", WhatsNewState.ReadSeenVersion(Dir, "pi"));
        Assert.Equal(new[] { "seen-pi.json" },
            Directory.GetFiles(Dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void WriteSeenVersion_TwoHosts_DoNotInterfere()
    {
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.7.0");
        WhatsNewState.WriteSeenVersion(Dir, "cursor", "4.8.0");
        Assert.Equal("4.7.0", WhatsNewState.ReadSeenVersion(Dir, "pi"));
        Assert.Equal("4.8.0", WhatsNewState.ReadSeenVersion(Dir, "cursor"));
        Assert.Null(WhatsNewState.ReadSeenVersion(Dir, "codex"));
    }

    [Fact]
    public void WriteSeenVersion_HostilePath_StaysInsideDirectory()
    {
        WhatsNewState.WriteSeenVersion(Dir, "../../etc/passwd", "4.8.0");
        Assert.True(File.Exists(Path.Combine(Dir, "seen-other.json")));
        Assert.Equal("4.8.0", WhatsNewState.ReadSeenVersion(Dir, "x/y"));
    }

    [Fact]
    public void ReadSeenVersion_StaleTempFile_IsIgnored()
    {
        Put("seen-pi.json." + Guid.NewGuid().ToString("N") + ".tmp", "{\"version\":\"9.9.9\"}");
        Assert.Null(WhatsNewState.ReadSeenVersion(Dir, "pi"));
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.8.0");
        Assert.Equal("4.8.0", WhatsNewState.ReadSeenVersion(Dir, "pi"));
    }

    [Fact]
    public void WriteSeenVersion_NullVersion_WritesNothing_KeepsExistingFile()
    {
        const string original = "{\"version\":\"4.8.0\",\"availableSeen\":{\"stable\":\"4.9.0\"}}";
        Put("seen-pi.json", original);
        WhatsNewState.WriteSeenVersion(Dir, "pi", null!);
        Assert.Equal(original, File.ReadAllText(Path.Combine(Dir, "seen-pi.json")));
        Assert.Equal(new[] { "seen-pi.json" },
            Directory.GetFiles(Dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void WriteSeenVersion_UnwritablePath_DoesNotThrow()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "i am a file");
        var ex = Record.Exception(() =>
            WhatsNewState.WriteSeenVersion(Path.Combine(blocker, "whats-new"), "pi", "4.8.0"));
        Assert.Null(ex);
        Assert.Null(WhatsNewState.ReadSeenVersion(Path.Combine(blocker, "whats-new"), "pi"));
    }

    [Fact]
    public void ReadSeen_VersionAndAvailableSeen()
    {
        Put("seen-pi.json", "{\"version\":\"4.8.0\",\"availableSeen\":{\"stable\":\"4.9.0\",\"pre\":\"4.10.0-beta.1\"}}");
        var seen = WhatsNewState.ReadSeen(Dir, "pi");
        Assert.Equal("4.8.0", seen.Version);
        Assert.Equal(2, seen.AvailableSeen.Count);
        Assert.Equal("4.9.0", seen.AvailableSeen["stable"]);
        Assert.Equal("4.10.0-beta.1", seen.AvailableSeen["pre"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"4.9.0\"")]
    [InlineData("{\"stable\":4}")]
    [InlineData("{\"beta\":\"4.9.0\"}")]
    public void ReadSeen_AvailableSeenWrongShapes_Ignored(string available)
    {
        Put("seen-pi.json", "{\"version\":\"4.8.0\",\"availableSeen\":" + available + "}");
        var seen = WhatsNewState.ReadSeen(Dir, "pi");
        Assert.Equal("4.8.0", seen.Version);
        Assert.Empty(seen.AvailableSeen);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    public void ReadSeen_CorruptOrMissing_IsEmpty(string content)
    {
        Assert.Null(WhatsNewState.ReadSeen(Dir, "pi").Version);
        Put("seen-pi.json", content);
        var seen = WhatsNewState.ReadSeen(Dir, "pi");
        Assert.NotNull(seen);
        Assert.Null(seen.Version);
        Assert.Empty(seen.AvailableSeen);
    }

    [Fact]
    public void ReadSeen_OnlyKnownChannelKeysKept_WhenMixed()
    {
        Put("seen-pi.json", "{\"availableSeen\":{\"stable\":\"4.9.0\",\"beta\":\"x\",\"pre\":5}}");
        var seen = WhatsNewState.ReadSeen(Dir, "pi");
        Assert.Null(seen.Version);
        Assert.Single(seen.AvailableSeen);
        Assert.Equal("4.9.0", seen.AvailableSeen["stable"]);
    }

    [Fact]
    public void WriteSeenVersion_ThenWriteAvailableSeen_KeepsBoth()
    {
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.8.0");
        WhatsNewState.WriteAvailableSeen(Dir, "pi", "stable", "4.9.0");
        var seen = WhatsNewState.ReadSeen(Dir, "pi");
        Assert.Equal("4.8.0", seen.Version);
        Assert.Equal("4.9.0", seen.AvailableSeen["stable"]);
    }

    [Fact]
    public void WriteAvailableSeen_ThenWriteSeenVersion_KeepsBoth()
    {
        WhatsNewState.WriteAvailableSeen(Dir, "pi", "stable", "4.9.0");
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.8.0");
        var seen = WhatsNewState.ReadSeen(Dir, "pi");
        Assert.Equal("4.8.0", seen.Version);
        Assert.Equal("4.9.0", seen.AvailableSeen["stable"]);
    }

    [Fact]
    public void WriteAvailableSeen_PerChannel_DoesNotOverwriteOtherChannel()
    {
        WhatsNewState.WriteAvailableSeen(Dir, "pi", "stable", "4.9.0");
        WhatsNewState.WriteAvailableSeen(Dir, "pi", "pre", "4.10.0-beta.1");
        var seen = WhatsNewState.ReadSeen(Dir, "pi");
        Assert.Equal("4.9.0", seen.AvailableSeen["stable"]);
        Assert.Equal("4.10.0-beta.1", seen.AvailableSeen["pre"]);
        Assert.Equal(
            "{\"availableSeen\":{\"stable\":\"4.9.0\",\"pre\":\"4.10.0-beta.1\"}}",
            File.ReadAllText(Path.Combine(Dir, "seen-pi.json")));
    }

    [Fact]
    public void WriteAvailableSeen_PreFirstThenStable_KeysInFixedOrder()
    {
        WhatsNewState.WriteAvailableSeen(Dir, "pi", "pre", "4.10.0-beta.1");
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.8.0");
        WhatsNewState.WriteAvailableSeen(Dir, "pi", "stable", "4.9.0");
        Assert.Equal(
            "{\"version\":\"4.8.0\",\"availableSeen\":{\"stable\":\"4.9.0\",\"pre\":\"4.10.0-beta.1\"}}",
            File.ReadAllText(Path.Combine(Dir, "seen-pi.json")));
    }

    [Fact]
    public void WriteAvailableSeen_NoSeenFile_WritesOnlyAvailableSeen()
    {
        WhatsNewState.WriteAvailableSeen(Dir, "pi", "stable", "4.9.0");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "seen-pi.json")));
        var props = doc.RootElement.EnumerateObject().ToList();
        Assert.Single(props);
        Assert.Equal("availableSeen", props[0].Name);
        Assert.Null(WhatsNewState.ReadSeenVersion(Dir, "pi"));
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("../x")]
    [InlineData("")]
    [InlineData(null)]
    public void WriteAvailableSeen_UnknownChannel_WritesNothing(string? channel)
    {
        WhatsNewState.WriteAvailableSeen(Dir, "pi", channel!, "4.9.0");
        Assert.False(File.Exists(Path.Combine(Dir, "seen-pi.json")));
        Assert.False(Directory.Exists(Dir) && Directory.GetFiles(Dir).Length > 0);
    }

    [Fact]
    public void WriteSeenVersion_OverCorruptFile_WritesVersionOnly()
    {
        Put("seen-pi.json", "not json");
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.8.0");
        Assert.Equal("{\"version\":\"4.8.0\"}", File.ReadAllText(Path.Combine(Dir, "seen-pi.json")));
    }

    [Fact]
    public void WriteAvailableSeen_HostilePath_StaysInsideDirectory()
    {
        WhatsNewState.WriteAvailableSeen(Dir, "../../x", "stable", "4.9.0");
        Assert.True(File.Exists(Path.Combine(Dir, "seen-other.json")));
        Assert.Equal("4.9.0", WhatsNewState.ReadSeen(Dir, "other").AvailableSeen["stable"]);
        Assert.False(File.Exists(Path.Combine(_root, "seen-other.json")));
    }

    [Fact]
    public void WriteAvailableSeen_UnwritablePath_DoesNotThrow()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "i am a file");
        var ex = Record.Exception(() =>
            WhatsNewState.WriteAvailableSeen(Path.Combine(blocker, "whats-new"), "pi", "stable", "4.9.0"));
        Assert.Null(ex);
        Assert.Empty(WhatsNewState.ReadSeen(Path.Combine(blocker, "whats-new"), "pi").AvailableSeen);
    }

    [Fact]
    public void WriteAvailableSeen_LeavesNoTempFiles()
    {
        WhatsNewState.WriteAvailableSeen(Dir, "pi", "stable", "4.9.0");
        WhatsNewState.WriteAvailableSeen(Dir, "pi", "pre", "4.10.0-beta.1");
        WhatsNewState.WriteSeenVersion(Dir, "pi", "4.8.0");
        Assert.Equal(new[] { "seen-pi.json" },
            Directory.GetFiles(Dir).Select(Path.GetFileName).ToArray());
    }

    private void Stamp(string host, DateTimeOffset at) =>
        Put(WhatsNewState.StampFileName(host),
            "{\"at\":\"" + at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") + "\"}");

    [Fact]
    public void IsDirectStampFresh_29Days_True()
    {
        Stamp("pi", Now - TimeSpan.FromDays(29));
        Assert.True(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
    }

    [Fact]
    public void IsDirectStampFresh_Exactly30Days_True()
    {
        Stamp("pi", Now - TimeSpan.FromDays(30));
        Assert.True(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
    }

    [Fact]
    public void IsDirectStampFresh_31Days_False()
    {
        Stamp("pi", Now - TimeSpan.FromDays(31));
        Assert.False(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
    }

    [Fact]
    public void IsDirectStampFresh_JustNow_True()
    {
        Stamp("pi", Now);
        Assert.True(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
    }

    [Fact]
    public void IsDirectStampFresh_SlightlyInFuture_True()
    {
        Stamp("pi", Now + TimeSpan.FromHours(23));
        Assert.True(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
    }

    [Fact]
    public void IsDirectStampFresh_FarInFuture_False()
    {
        Stamp("pi", Now + TimeSpan.FromDays(2));
        Assert.False(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
        Stamp("pi", Now + TimeSpan.FromDays(3650));
        Assert.False(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
    }

    [Fact]
    public void IsDirectStampFresh_Missing_False() =>
        Assert.False(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"at\":12345}")]
    [InlineData("{\"at\":null}")]
    [InlineData("{\"at\":\"yesterday\"}")]
    [InlineData("{\"at\":\"\"}")]
    public void IsDirectStampFresh_Corrupt_False(string content)
    {
        Put(WhatsNewState.StampFileName("pi"), content);
        Assert.False(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
    }

    [Fact]
    public void IsDirectStampFresh_OtherHostStamp_DoesNotCount()
    {
        Stamp("cursor", Now);
        Assert.False(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
        Assert.True(WhatsNewState.IsDirectStampFresh(Dir, "cursor", Now));
    }

    [Fact]
    public void IsDirectStampFresh_OffsetTimestamp_UsesInstant()
    {
        // 29 days ago expressed with a +05:00 offset.
        var at = (Now - TimeSpan.FromDays(29)).ToOffset(TimeSpan.FromHours(5));
        Put(WhatsNewState.StampFileName("pi"), "{\"at\":\"" + at.ToString("o") + "\"}");
        Assert.True(WhatsNewState.IsDirectStampFresh(Dir, "pi", Now));
    }

    [Fact]
    public void IsDirectStampFresh_StampAsJavaScriptWritesIt_ParsesAndAges()
    {
        // JavaScript's Date.prototype.toISOString: milliseconds and a literal Z.
        Put(WhatsNewState.StampFileName("pi"), "{\"at\":\"2026-11-15T20:24:56.123Z\"}");
        var written = new DateTimeOffset(2026, 11, 15, 20, 24, 56, 123, TimeSpan.Zero);
        Assert.True(WhatsNewState.IsDirectStampFresh(Dir, "pi", written + TimeSpan.FromDays(29)));
        Assert.False(WhatsNewState.IsDirectStampFresh(Dir, "pi", written + TimeSpan.FromDays(31)));
    }
}
