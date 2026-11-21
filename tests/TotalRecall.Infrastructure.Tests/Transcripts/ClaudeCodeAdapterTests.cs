using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class ClaudeCodeAdapterTests : IDisposable
{
    private const string Cwd = "/work/example-repo";
    private readonly ClaudeFixtureHome _home = new();

    public void Dispose() => _home.Dispose();

    private ClaudeCodeAdapter Adapter() => new(_home.ProjectsDir);

    private static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private string DirFor(string cwd)
    {
        var d = Path.Combine(_home.ProjectsDir, ClaudeFixtureHome.EncodeDir(cwd));
        Directory.CreateDirectory(d);
        return d;
    }

    [Theory]
    [InlineData("/c", "/c")]
    [InlineData(" /c ", "/c")]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void ResolveRoot_UsesClaudeConfigDir_WhenNonEmpty(string? dir, string? expectedDir)
    {
        var profile = Path.Combine(Path.GetTempPath(), "profile");
        var expected = expectedDir is not null
            ? Path.Combine(expectedDir, "projects")
            : Path.Combine(profile, ".claude", "projects");
        Assert.Equal(expected, ClaudeCodeAdapter.ResolveRoot(dir, profile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ResolveRoot_NoConfigDirAndNoUserProfile_IsEmpty_AndNothingIsDetected(string? dir)
    {
        var root = ClaudeCodeAdapter.ResolveRoot(dir, "");
        Assert.Equal("", root);
        var a = new ClaudeCodeAdapter(root, ClaudeCodeAdapter.MaxLineBytes);
        Assert.False(a.Detect());
        Assert.Empty(a.EnumerateSessions().Sources);
    }

    [Fact]
    public void ResolveRoot_ConfigDirWinsEvenWithoutUserProfile()
    {
        Assert.Equal(Path.Combine("/c", "projects"), ClaudeCodeAdapter.ResolveRoot("/c", ""));
    }

    [Fact]
    public void ZeroSizeSource_IsNeverOpened()
    {
        // A FIFO or device node named <id>.jsonl stats with size 0 and would block in open(2); a zero size must
        // therefore be answered without opening. The path here is a non-empty regular file: opening it would show.
        var s = _home.Place("decision", Cwd, "zero");
        _home.Write(s, Encoding.UTF8.GetBytes("{\"cwd\":\"/p\"}\nsecond\n"));
        var zero = Source(s) with { Size = 0 };
        var a = Adapter();

        var head = a.ReadHead(zero);
        Assert.Null(head.Fingerprint);
        Assert.Null(head.Cwd);
        Assert.True(a.IsLineStart(zero, 0));
        Assert.False(a.IsLineStart(zero, 9));
        var stats = new ReadStats();
        Assert.Empty(a.ReadRecords(zero, 0, stats, CancellationToken.None));
        Assert.Equal(0, stats.SkippedLines);

        Assert.NotNull(a.ReadHead(Source(s)).Fingerprint);   // the same file with its real size is read
    }

    [Fact]
    public void Constants_AndHost()
    {
        Assert.Equal("claude-code", ClaudeCodeAdapter.HostName);
        Assert.Equal(16 * 1024 * 1024, ClaudeCodeAdapter.MaxLineBytes);
        Assert.Equal(200, ClaudeCodeAdapter.HeadLines);
        var a = Adapter();
        Assert.Equal("claude-code", a.Host);
        Assert.Equal(_home.ProjectsDir, a.Root);
    }

    [Fact]
    public void Detect_FalseWithoutTheDirectory()
    {
        var missing = new ClaudeCodeAdapter(Path.Combine(_home.Root, "nope", "projects"));
        Assert.False(missing.Detect());
        Assert.True(Adapter().Detect());
        var e = missing.EnumerateSessions();
        Assert.Empty(e.Sources);
        Assert.Equal(0, e.Skipped);
    }

    [Fact]
    public void Enumerate_OneLevelOnly_SkipsBadNames()
    {
        var good = _home.Place("decision", Cwd, "good-id.1_x");
        var dir = DirFor(Cwd);
        File.WriteAllText(Path.Combine(dir, "a b.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(dir, new string('a', 129) + ".jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(dir, new string('a', 128) + ".jsonl"), "{}\n");
        // A name with a newline cannot be created on Windows.
        var newlineName = !OperatingSystem.IsWindows();
        if (newlineName) File.WriteAllText(Path.Combine(dir, "nl\n.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(dir, ".jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
        // A subagent file beneath the session folder, and a loose file directly in the root.
        Directory.CreateDirectory(Path.Combine(dir, "good-id.1_x", "subagents"));
        File.WriteAllText(Path.Combine(dir, "good-id.1_x", "subagents", "x.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(_home.ProjectsDir, "loose.jsonl"), "{}\n");

        var e = Adapter().EnumerateSessions();
        Assert.Equal(new[] { new string('a', 128), "good-id.1_x" }, e.Sources.Select(s => s.HostSessionId).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        // "a b", 129 characters, "nl\n" and the empty id.
        Assert.Equal(newlineName ? 4 : 3, e.Skipped);
        Assert.Contains(e.Sources, s => s.Path == good.Path);
    }

    [Fact]
    public void Enumerate_SymlinksBeneathRoot_AreNotRead()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = _home.Place("decision", Cwd, "real");
        var outside = Path.Combine(_home.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "viaDir.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(outside, "target.jsonl"), "{}\n");
        var dir = DirFor(Cwd);
        File.CreateSymbolicLink(Path.Combine(dir, "linkedfile.jsonl"), Path.Combine(outside, "target.jsonl"));
        Directory.CreateSymbolicLink(Path.Combine(_home.ProjectsDir, "linked-project"), outside);

        var e = Adapter().EnumerateSessions();
        Assert.Equal(new[] { "real" }, e.Sources.Select(s => s.HostSessionId).ToArray());
        Assert.Equal(real.Path, e.Sources[0].Path);
        Assert.Equal(0, e.Skipped);
    }

    [Fact]
    public void Enumerate_RootItselfASymlink_IsFollowed()
    {
        if (OperatingSystem.IsWindows()) return;
        var s = _home.Place("decision", Cwd, "viaroot");
        var link = Path.Combine(_home.Root, "projects-link");
        Directory.CreateSymbolicLink(link, _home.ProjectsDir);
        var a = new ClaudeCodeAdapter(link);
        Assert.True(a.Detect());
        var e = a.EnumerateSessions();
        Assert.Single(e.Sources);
        Assert.Equal("viaroot", e.Sources[0].HostSessionId);
        Assert.Equal(Path.Combine(link, ClaudeFixtureHome.EncodeDir(Cwd), "viaroot.jsonl"), e.Sources[0].Path);
        Assert.NotNull(s);
    }

    [Fact]
    public void Enumerate_SameIdInTwoDirs_ReturnsBoth()
    {
        var a = _home.Place("decision", "/work/one", "same-id");
        var b = _home.Place("decision", "/work/two", "same-id");
        var e = Adapter().EnumerateSessions();
        Assert.Equal(2, e.Sources.Count);
        Assert.All(e.Sources, s => Assert.Equal("same-id", s.HostSessionId));
        Assert.Equal(new[] { a.Path, b.Path }.OrderBy(x => x, StringComparer.Ordinal), e.Sources.Select(s => s.Path).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(0, e.Skipped);
    }

    [Fact]
    public void Enumerate_SourceCarriesSizeAndMtime()
    {
        var s = _home.Place("decision", Cwd, "sz");
        var t = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 89, TimeSpan.Zero);
        _home.SetMtime(s, t);
        var src = Assert.Single(Adapter().EnumerateSessions().Sources);
        Assert.Equal(new FileInfo(s.Path).Length, src.Size);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(s.Path), TimeSpan.Zero).ToUnixTimeMilliseconds(), src.MtimeMs);
        Assert.Equal(t.ToUnixTimeMilliseconds() / 1000, src.MtimeMs / 1000);
    }

    private SessionSource Source(PlacedSession s) => Assert.Single(Adapter().EnumerateSessions().Sources, x => x.Path == s.Path);

    [Fact]
    public void ReadHead_FirstCwdEntrypointTimestamp_AndFingerprintOfFirstLine()
    {
        var s = _home.Place("decision", Cwd, "head1");
        var bytes = File.ReadAllBytes(s.Path);
        var firstLine = Encoding.UTF8.GetString(bytes, 0, Array.IndexOf(bytes, (byte)'\n'));
        using var first = System.Text.Json.JsonDocument.Parse(firstLine);

        var head = Adapter().ReadHead(Source(s));
        Assert.Equal(Cwd, head.Cwd);
        Assert.Equal(Sha(firstLine), head.Fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", head.Fingerprint!);
        Assert.NotNull(head.Entrypoint);
        Assert.NotNull(head.FirstTimestamp);
        Assert.Equal(first.RootElement.GetProperty("timestamp").GetDateTimeOffset().ToUniversalTime(), head.FirstTimestamp!.Value);
    }

    [Fact]
    public void ReadHead_FieldsMayComeFromDifferentLines_AndSkipBadLines()
    {
        var s = _home.Place("decision", Cwd, "head2");
        _home.Write(s, Encoding.UTF8.GetBytes(
            "not json\n[1,2]\n{\"type\":\"x\"}\n" +
            "{\"cwd\":\"/first\",\"timestamp\":\"2026-02-03T04:05:06+02:00\"}\n" +
            "{\"cwd\":\"/second\",\"entrypoint\":\"sdk-py\",\"timestamp\":\"2030-01-01T00:00:00Z\"}\n"));
        var head = Adapter().ReadHead(Source(s));
        Assert.Equal("/first", head.Cwd);
        Assert.Equal("sdk-py", head.Entrypoint);
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 2, 5, 6, TimeSpan.Zero), head.FirstTimestamp);
        Assert.Equal(Sha("not json"), head.Fingerprint);
    }

    [Fact]
    public void ReadHead_NoCompleteLine_NullFingerprint()
    {
        var s = _home.Place("decision", Cwd, "partial");
        _home.Write(s, Encoding.UTF8.GetBytes("{\"cwd\":\"/p\",\"entrypoint\":\"cli\""));
        var head = Adapter().ReadHead(Source(s));
        Assert.Null(head.Fingerprint);
        Assert.Null(head.Cwd);
        Assert.Null(head.Entrypoint);
        Assert.Null(head.FirstTimestamp);

        _home.Write(s, Array.Empty<byte>());
        Assert.Null(Adapter().ReadHead(Source(s)).Fingerprint);
    }

    [Fact]
    public void ReadHead_FirstLineOverLimit_NullFingerprint()
    {
        var s = _home.Place("decision", Cwd, "biggest");
        var tooBig = "{\"cwd\":\"/big\",\"pad\":\"" + new string('x', 3000) + "\"}\n{\"cwd\":\"/second\"}\n";
        _home.Write(s, Encoding.UTF8.GetBytes(tooBig));
        var head = new ClaudeCodeAdapter(_home.ProjectsDir, 1024).ReadHead(Source(s));
        Assert.Null(head.Fingerprint);
    }

    [Fact]
    public void ReadHead_StopsAfter200Lines()
    {
        var s = _home.Place("decision", Cwd, "late");
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++) sb.Append("{\"type\":\"noise\"}\n");
        sb.Append("{\"cwd\":\"/too-late\",\"entrypoint\":\"cli\",\"timestamp\":\"2026-01-01T00:00:00Z\"}\n");
        _home.Write(s, Encoding.UTF8.GetBytes(sb.ToString()));
        var head = Adapter().ReadHead(Source(s));
        Assert.Null(head.Cwd);
        Assert.Null(head.Entrypoint);
        Assert.Null(head.FirstTimestamp);
        Assert.Equal(Sha("{\"type\":\"noise\"}"), head.Fingerprint);

        // On line 200 it is still found.
        var sb2 = new StringBuilder();
        for (var i = 0; i < 199; i++) sb2.Append("{\"type\":\"noise\"}\n");
        sb2.Append("{\"cwd\":\"/in-time\"}\n");
        _home.Write(s, Encoding.UTF8.GetBytes(sb2.ToString()));
        Assert.Equal("/in-time", Adapter().ReadHead(Source(s)).Cwd);
    }

    [Fact]
    public void ReadHead_GrowingFile_SameFingerprint()
    {
        var s = _home.Place("decision", Cwd, "grow");
        var before = Adapter().ReadHead(Source(s));
        _home.AppendExchange(s, "more", "again", 500);
        _home.AppendPartial(s, "{\"half\":");
        var after = Adapter().ReadHead(Source(s));
        Assert.NotNull(before.Fingerprint);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void ReadHead_BomAndCrlfFirstLine_FingerprintOfBytesWithoutLf()
    {
        var s = _home.Place("decision", Cwd, "bomcrlf");
        var body = Encoding.UTF8.GetBytes("{\"cwd\":\"/bom\"}\r\n{\"entrypoint\":\"cli\"}\r\n");
        _home.Write(s, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray());
        var head = Adapter().ReadHead(Source(s));
        Assert.Equal("/bom", head.Cwd);
        Assert.Equal("cli", head.Entrypoint);
        // The first line's bytes without the 0x0A: the BOM is not part of the line, the CR is.
        Assert.Equal(Sha("{\"cwd\":\"/bom\"}\r"), head.Fingerprint);
    }

    [Fact]
    public void IsLineStart_TrueAtZeroAndAfterNewline_FalseMidLine()
    {
        var s = _home.Place("decision", Cwd, "ls");
        _home.Write(s, Encoding.UTF8.GetBytes("abc\ndef\n"));
        var src = Source(s);
        var a = Adapter();
        Assert.True(a.IsLineStart(src, 0));
        Assert.False(a.IsLineStart(src, 1));
        Assert.False(a.IsLineStart(src, 3));
        Assert.True(a.IsLineStart(src, 4));
        Assert.False(a.IsLineStart(src, 5));
        Assert.True(a.IsLineStart(src, 8));
        Assert.False(a.IsLineStart(src, 9));
    }

    [Fact]
    public void ReadRecords_ReadsLines_YieldsNoRecordYet_AndCountsOverLimitLines()
    {
        var s = _home.Place("decision", Cwd, "rr");
        _home.Write(s, Encoding.UTF8.GetBytes("{\"a\":1}\n" + new string('x', 3000) + "\n{\"b\":2}\n"));
        var stats = new ReadStats();
        var a = new ClaudeCodeAdapter(_home.ProjectsDir, 1024);
        Assert.Empty(a.ReadRecords(Source(s), 0, stats, CancellationToken.None));
        Assert.Equal(1, stats.SkippedLines);
    }
}
