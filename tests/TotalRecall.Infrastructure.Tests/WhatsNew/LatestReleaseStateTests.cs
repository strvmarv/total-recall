using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class LatestReleaseStateTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tr-latest-" + Guid.NewGuid().ToString("N"));

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

    private static readonly DateTimeOffset T1 = new(2026, 11, 16, 10, 0, 0, 123, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 11, 16, 11, 30, 5, 7, TimeSpan.Zero);

    [Fact]
    public void FileName_PerChannel()
    {
        Assert.Equal("latest-stable.json", LatestReleaseState.FileName("stable"));
        Assert.Equal("latest-pre.json", LatestReleaseState.FileName("pre"));
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        LatestReleaseState.Write(Dir, "stable", new LatestReleaseFile("4.9.0", "Fixes things.", T1, T2));
        var r = LatestReleaseState.Read(Dir, "stable");
        Assert.NotNull(r);
        Assert.Equal("4.9.0", r!.Version);
        Assert.Equal("Fixes things.", r.Summary);
        Assert.Equal(T1, r.CheckedAt);
        Assert.Equal(T2, r.LastAttempt);
    }

    [Fact]
    public void Write_OmitsNullKeys()
    {
        LatestReleaseState.Write(Dir, "pre", new LatestReleaseFile(null, null, null, T2));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "latest-pre.json")));
        var props = doc.RootElement.EnumerateObject().ToList();
        Assert.Single(props);
        Assert.Equal("lastAttempt", props[0].Name);
    }

    [Fact]
    public void Write_KeysInOrder()
    {
        LatestReleaseState.Write(Dir, "stable", new LatestReleaseFile("4.9.0", "s", T1, T2));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "latest-stable.json")));
        Assert.Equal(new[] { "version", "summary", "checkedAt", "lastAttempt" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Write_TimeFormat_IsIsoUtcWithZ()
    {
        var t = DateTimeOffset.Parse("2026-11-16T12:00:00.5+02:00");
        LatestReleaseState.Write(Dir, "stable", new LatestReleaseFile(null, null, t, null));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "latest-stable.json")));
        Assert.Equal("2026-11-16T10:00:00.500Z", doc.RootElement.GetProperty("checkedAt").GetString());
        Assert.Equal("2026-11-16T10:00:00.500Z", LatestReleaseState.FormatTime(t));
    }

    [Fact]
    public void Read_Missing_IsNull()
    {
        Assert.Null(LatestReleaseState.Read(Dir, "stable"));
        Directory.CreateDirectory(Dir);
        Assert.Null(LatestReleaseState.Read(Dir, "stable"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("42")]
    public void Read_Corrupt_IsNull(string content)
    {
        Put("latest-stable.json", content);
        Assert.Null(LatestReleaseState.Read(Dir, "stable"));
    }

    [Fact]
    public void Read_DirectoryInsteadOfFile_IsNull()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "latest-stable.json"));
        Assert.Null(LatestReleaseState.Read(Dir, "stable"));
    }

    private static string Padded(int totalBytes)
    {
        const string json = "{\"version\":\"4.9.0\"}";
        return json + new string(' ', totalBytes - json.Length);
    }

    [Fact]
    public void Read_OverSizeCap_IsNull()
    {
        var text = Padded(65_537);
        Assert.Equal(65_537, text.Length);
        Put("latest-stable.json", text);
        Assert.Null(LatestReleaseState.Read(Dir, "stable"));
    }

    [Fact]
    public void Read_ExactlyOneByteOverCap_IsNull()
    {
        var text = Padded((int)LatestReleaseState.MaxBytes + 1);
        Assert.Equal(65_537, text.Length);
        Put("latest-stable.json", text);
        Assert.Null(LatestReleaseState.Read(Dir, "stable"));
    }

    [Fact]
    public void Read_Utf8Bom_IsTolerated()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(Path.Combine(Dir, "latest-stable.json"),
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("{\"version\":\"4.9.0\"}")).ToArray());
        Assert.Equal("4.9.0", LatestReleaseState.Read(Dir, "stable")!.Version);
    }

    [Fact]
    public void Read_AtSizeCap_IsRead()
    {
        var text = Padded(65_536);
        Assert.Equal((int)LatestReleaseState.MaxBytes, text.Length);
        Put("latest-stable.json", text);
        var r = LatestReleaseState.Read(Dir, "stable");
        Assert.NotNull(r);
        Assert.Equal("4.9.0", r!.Version);
    }

    [Theory]
    [InlineData("\"v4.9.0\"")]
    [InlineData("\"4.9.0+build\"")]
    [InlineData("\"4.9\"")]
    [InlineData("7")]
    public void Read_InvalidVersion_DropsVersionAndSummary_KeepsTimes(string versionJson)
    {
        Put("latest-stable.json",
            "{\"version\":" + versionJson + ",\"summary\":\"Hello\",\"checkedAt\":\"2026-11-16T10:00:00.123Z\",\"lastAttempt\":\"2026-11-16T11:30:05.007Z\"}");
        var r = LatestReleaseState.Read(Dir, "stable");
        Assert.NotNull(r);
        Assert.Null(r!.Version);
        Assert.Null(r.Summary);
        Assert.Equal(T1, r.CheckedAt);
        Assert.Equal(T2, r.LastAttempt);
    }

    [Fact]
    public void Read_SixtyFiveCharacterVersion_IsDropped()
    {
        var v = "4.9.0-" + new string('a', 59);
        Assert.Equal(65, v.Length);
        Put("latest-stable.json", "{\"version\":\"" + v + "\",\"summary\":\"Hello\"}");
        var r = LatestReleaseState.Read(Dir, "stable");
        Assert.NotNull(r);
        Assert.Null(r!.Version);
        Assert.Null(r.Summary);
    }

    [Fact]
    public void Read_SixtyFourCharacterVersion_IsKept()
    {
        var v = "4.9.0-" + new string('a', 58);
        Assert.Equal(64, v.Length);
        Put("latest-stable.json", "{\"version\":\"" + v + "\"}");
        Assert.Equal(v, LatestReleaseState.Read(Dir, "stable")!.Version);
    }

    [Fact]
    public void Read_Summary_IsSanitisedAgain()
    {
        Put("latest-stable.json",
            "{\"version\":\"4.9.0\",\"summary\":\"\\u001b[31mFixed\\u001b[0m [the cache](https://x.y) bug\\nnow\"}");
        Assert.Equal("Fixed the cache bug now", LatestReleaseState.Read(Dir, "stable")!.Summary);

        var longSummary = string.Join(" ", Enumerable.Repeat("word", 101))[..500];
        Assert.Equal(500, longSummary.Length);
        Put("latest-pre.json",
            "{\"version\":\"4.10.0-beta.1\",\"summary\":\"" + longSummary + "\"}");
        var s = LatestReleaseState.Read(Dir, "pre")!.Summary;
        Assert.NotNull(s);
        Assert.True(s!.Length <= 200);
    }

    [Fact]
    public void Read_EmptyOrNonStringSummary_IsNull()
    {
        Put("latest-stable.json", "{\"version\":\"4.9.0\",\"summary\":\"\\u001b[0m   \"}");
        Assert.Null(LatestReleaseState.Read(Dir, "stable")!.Summary);
        Put("latest-stable.json", "{\"version\":\"4.9.0\",\"summary\":5}");
        Assert.Null(LatestReleaseState.Read(Dir, "stable")!.Summary);
    }

    [Theory]
    [InlineData("\"yesterday\"")]
    [InlineData("5")]
    public void Read_BadTimes_AreNull(string timeJson)
    {
        Put("latest-stable.json",
            "{\"version\":\"4.9.0\",\"checkedAt\":" + timeJson + ",\"lastAttempt\":" + timeJson + "}");
        var r = LatestReleaseState.Read(Dir, "stable");
        Assert.NotNull(r);
        Assert.Equal("4.9.0", r!.Version);
        Assert.Null(r.CheckedAt);
        Assert.Null(r.LastAttempt);
    }

    [Fact]
    public void Read_TimeWithOffset_UsesInstant()
    {
        Put("latest-stable.json", "{\"checkedAt\":\"2026-11-16T12:00:00.000+02:00\"}");
        var r = LatestReleaseState.Read(Dir, "stable");
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero), r!.CheckedAt);
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("../x")]
    [InlineData("")]
    public void Read_UnknownChannel_IsNull(string channel)
    {
        Put("latest-stable.json", "{\"version\":\"4.9.0\"}");
        Assert.Null(LatestReleaseState.Read(Dir, channel));
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("../x")]
    public void Write_UnknownChannel_WritesNothing(string channel)
    {
        LatestReleaseState.Write(Dir, channel, new LatestReleaseFile("4.9.0", null, T1, T1));
        Assert.False(Directory.Exists(Dir));
        Assert.False(Directory.Exists(_root) && Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public void Write_LeavesNoTempFiles()
    {
        LatestReleaseState.Write(Dir, "stable", new LatestReleaseFile("4.8.0", null, T1, T1));
        LatestReleaseState.Write(Dir, "stable", new LatestReleaseFile("4.9.0", null, T1, T2));
        Assert.Equal(new[] { "latest-stable.json" },
            Directory.GetFiles(Dir).Select(Path.GetFileName).ToArray());
        Assert.Equal("4.9.0", LatestReleaseState.Read(Dir, "stable")!.Version);
    }

    [Fact]
    public void Write_UnwritableDir_DoesNotThrow()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "i am a file");
        var ex = Record.Exception(() =>
            LatestReleaseState.Write(Path.Combine(blocker, "whats-new"), "stable", new LatestReleaseFile("4.9.0", null, T1, T1)));
        Assert.Null(ex);
    }
}
