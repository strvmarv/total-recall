using System.Text.Json;
using TotalRecall.Cli;
using TotalRecall.Cli.Commands;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

[Collection("ConsoleCapture")]
public sealed class WhatsNewCommandTests : IDisposable
{
    private const string FirstSeenLine =
        "total-recall 4.8.0: Faster search. All releases: https://github.com/strvmarv/total-recall/releases";
    private const string UpdatedLine =
        "total-recall updated 4.7.0 -> 4.8.0: Faster search. More: https://github.com/strvmarv/total-recall/releases/tag/v4.8.0";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tr-whatsnew-cmd-" + Guid.NewGuid().ToString("N"));

    private string StateDir => Path.Combine(_root, "state", "whats-new");
    private string BinDir => Path.Combine(_root, "bin");

    public WhatsNewCommandTests() => Directory.CreateDirectory(BinDir);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteNotes(string version = "4.8.0", string? previous = "4.7.0", string summary = "Faster search.")
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"version\":").Append(JsonSerializer.Serialize(version));
        if (previous is not null) sb.Append(",\"previous\":").Append(JsonSerializer.Serialize(previous));
        sb.Append(",\"summary\":").Append(JsonSerializer.Serialize(summary));
        sb.Append(",\"sections\":{},\"url\":\"https://example.invalid/x\",\"releasesUrl\":\"https://example.invalid\"}");
        File.WriteAllText(Path.Combine(BinDir, "release-notes.json"), sb.ToString());
    }

    private void PutState(string name, string content)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(Path.Combine(StateDir, name), content);
    }

    private string SeenPath(string host) => Path.Combine(StateDir, "seen-" + host + ".json");

    private void PutLatest(string channel, string json) => PutState("latest-" + channel + ".json", json);

    private void PutLatestStable(string version = "4.9.0", string summary = "Faster sync.") =>
        PutLatest("stable", "{\"version\":\"" + version + "\",\"summary\":\"" + summary + "\"}");

    private const string AvailableLine =
        "total-recall 4.9.0 is available (you have 4.8.0): Faster sync. Update: pi update "
        + "More: https://github.com/strvmarv/total-recall/releases/tag/v4.9.0";

    private (int Exit, string Out) Run(
        string[] args, string version = "4.8.0", Func<bool>? enabled = null, TextWriter? writer = null)
        => Run(args, version, enabled, writer, null, null);

    private (int Exit, string Out) Run(
        string[] args, string version, Func<bool>? enabled, TextWriter? writer,
        Func<bool>? checkSetting, string? env)
    {
        var sw = writer ?? new StringWriter();
        var cmd = new WhatsNewCommand(version, StateDir, BinDir, enabled ?? (() => true), sw,
            checkSetting, checkSetting is null ? null : () => env);
        var exit = cmd.RunAsync(args).GetAwaiter().GetResult();
        return (exit, sw.ToString() ?? "");
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => throw new IOException("boom");
    }

    [Fact]
    public void FirstRun_PrintsFirstSeenLine_RecordsVersion_SecondRunSilent()
    {
        WriteNotes();
        var first = Run(new[] { "--host", "pi" });
        Assert.Equal(0, first.Exit);
        Assert.Equal(FirstSeenLine + Environment.NewLine, first.Out);
        Assert.Equal("{\"version\":\"4.8.0\"}", File.ReadAllText(SeenPath("pi")));

        var second = Run(new[] { "--host", "pi" });
        Assert.Equal(0, second.Exit);
        Assert.Equal("", second.Out);
    }

    [Fact]
    public void Updated_PrintsUpdatedLine()
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.7.0\"}");
        var r = Run(new[] { "--host", "pi" });
        Assert.Equal(UpdatedLine + Environment.NewLine, r.Out);
        Assert.Equal("{\"version\":\"4.8.0\"}", File.ReadAllText(SeenPath("pi")));
    }

    [Fact]
    public void JsonFormat_PrintsKindAndText_TextEqualsTextFormatLine()
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.7.0\"}");
        var r = Run(new[] { "--host", "pi", "--format", "json", "--no-mark" });
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal("updated", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(UpdatedLine, doc.RootElement.GetProperty("text").GetString());

        var text = Run(new[] { "--host", "pi", "--no-mark" });
        Assert.Equal(text.Out.TrimEnd(), doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void JsonFormat_FirstSeen_KindIsFirstSeen()
    {
        WriteNotes();
        var r = Run(new[] { "--host", "pi", "--format", "json" });
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal("first-seen", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(FirstSeenLine, doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void ClaudeCodeFormat_PrintsSystemMessage()
    {
        WriteNotes();
        var r = Run(new[] { "--host", "claude-code", "--format", "claude-code" });
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal(FirstSeenLine, doc.RootElement.GetProperty("systemMessage").GetString());
        Assert.Single(doc.RootElement.EnumerateObject());
        Assert.True(File.Exists(SeenPath("claude-code")));
    }

    [Theory]
    [InlineData("text", "")]
    [InlineData("json", "{}")]
    [InlineData("claude-code", "{}")]
    [InlineData("bogus", "")]
    public void NoNotice_PrintsFormatsEmptyOutput(string format, string expected)
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.8.0\"}");
        var r = Run(new[] { "--host", "pi", "--format", format });
        Assert.Equal(0, r.Exit);
        Assert.Equal(expected.Length == 0 ? "" : expected + Environment.NewLine, r.Out);
    }

    [Fact]
    public void BogusFormat_BehavesAsText()
    {
        WriteNotes();
        var r = Run(new[] { "--host", "pi", "--format", "bogus" });
        Assert.Equal(FirstSeenLine + Environment.NewLine, r.Out);
    }

    [Fact]
    public void NoMark_PrintsAndWritesNothing_NextRunPrintsAgain()
    {
        WriteNotes();
        var a = Run(new[] { "--host", "pi", "--no-mark" });
        var b = Run(new[] { "--host", "pi", "--no-mark" });
        Assert.Equal(FirstSeenLine + Environment.NewLine, a.Out);
        Assert.Equal(a.Out, b.Out);
        Assert.False(File.Exists(SeenPath("pi")));
        Assert.False(Directory.Exists(StateDir));
    }

    [Fact]
    public void DevBuild_PrintsNothing_WritesNothing()
    {
        WriteNotes("1.0.0");
        var r = Run(new[] { "--host", "pi" }, version: "1.0.0");
        Assert.Equal("", r.Out);
        Assert.False(Directory.Exists(StateDir));

        var u = Run(new[] { "--host", "pi" }, version: "unknown");
        Assert.Equal("", u.Out);
        Assert.False(Directory.Exists(StateDir));
    }

    [Fact]
    public void NoticeDisabled_PrintsNothing_WritesNothing()
    {
        WriteNotes();
        var r = Run(new[] { "--host", "pi" }, enabled: () => false);
        Assert.Equal("", r.Out);
        Assert.False(Directory.Exists(StateDir));
    }

    [Fact]
    public void NoticeEnabledThrows_PrintsEmptyOutput_ExitZero()
    {
        WriteNotes();
        var r = Run(new[] { "--host", "pi", "--format", "json" },
            enabled: () => throw new InvalidOperationException("x"));
        Assert.Equal(0, r.Exit);
        Assert.Equal("{}" + Environment.NewLine, r.Out);
        Assert.False(Directory.Exists(StateDir));
    }

    [Fact]
    public void GarbageSeenFile_CountsAsNeverTold()
    {
        WriteNotes();
        PutState("seen-pi.json", "not json {{{");
        var r = Run(new[] { "--host", "pi" });
        Assert.Equal(FirstSeenLine + Environment.NewLine, r.Out);
    }

    [Fact]
    public void SeenPathIsDirectory_CountsAsNeverTold()
    {
        WriteNotes();
        Directory.CreateDirectory(SeenPath("pi"));
        var r = Run(new[] { "--host", "pi" });
        Assert.Equal(FirstSeenLine + Environment.NewLine, r.Out);
    }

    [Fact]
    public void MissingNotes_FirstSeen_HasVersionAndNoSummary()
    {
        var r = Run(new[] { "--host", "pi" });
        Assert.Equal(
            "total-recall 4.8.0: All releases: https://github.com/strvmarv/total-recall/releases" + Environment.NewLine,
            r.Out);
    }

    [Fact]
    public void MissingNotes_Updated_FollowsSeenVersion()
    {
        PutState("seen-pi.json", "{\"version\":\"4.7.0\"}");
        var r = Run(new[] { "--host", "pi" });
        Assert.Equal(
            "total-recall updated 4.7.0 -> 4.8.0: All releases: https://github.com/strvmarv/total-recall/releases"
                + Environment.NewLine,
            r.Out);
    }

    [Fact]
    public void NotesOfAnotherVersion_AreIgnored()
    {
        WriteNotes(version: "4.9.0", previous: "4.8.0", summary: "Other.");
        PutState("seen-pi.json", "{\"version\":\"4.7.0\"}");
        var r = Run(new[] { "--host", "pi" });
        Assert.Equal(
            "total-recall updated 4.7.0 -> 4.8.0: All releases: https://github.com/strvmarv/total-recall/releases"
                + Environment.NewLine,
            r.Out);
    }

    [Theory]
    [InlineData("--host", "../x")]
    [InlineData("--host", "")]
    [InlineData("--host", "nonsense")]
    [InlineData("--format", "text")] // no --host at all
    public void UnknownOrMissingHost_BehavesAsOther_WritesInsideStateDir(string flag, string value)
    {
        WriteNotes();
        var r = Run(new[] { flag, value });
        Assert.Equal(FirstSeenLine + Environment.NewLine, r.Out);
        Assert.True(File.Exists(SeenPath("other")));
        Assert.Equal(new[] { SeenPath("other") }, Directory.GetFiles(StateDir));
        // Nothing was written outside the state dir.
        Assert.Empty(Directory.GetFiles(_root));
        Assert.Empty(Directory.GetFiles(BinDir, "seen*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "state")));
    }

    [Fact]
    public void NoArgs_BehavesAsOther()
    {
        WriteNotes();
        var r = Run(Array.Empty<string>());
        Assert.Equal(FirstSeenLine + Environment.NewLine, r.Out);
        Assert.True(File.Exists(SeenPath("other")));
    }

    [Fact]
    public void FlagWithoutValue_IsIgnored()
    {
        WriteNotes();
        var r = Run(new[] { "--format", "json", "--host" });
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal("first-seen", doc.RootElement.GetProperty("kind").GetString());
        Assert.True(File.Exists(SeenPath("other")));
    }

    [Fact]
    public void FlagFollowedByFlag_IsIgnored_AndNoMarkStillApplies()
    {
        WriteNotes();
        var r = Run(new[] { "--host", "--no-mark" });
        Assert.Equal(FirstSeenLine + Environment.NewLine, r.Out);
        Assert.False(Directory.Exists(StateDir));
    }

    [Fact]
    public void WriterThatThrows_RecordsNothing_ExitsZero()
    {
        WriteNotes();
        var r = Run(new[] { "--host", "pi" }, writer: new ThrowingWriter());
        Assert.Equal(0, r.Exit);
        Assert.False(File.Exists(SeenPath("pi")));
    }

    [Fact]
    public void ClaudeCodeHost_RecordsOwnFile_EvenWhenHermesSeenExists()
    {
        WriteNotes();
        PutState("seen-hermes.json", "{\"version\":\"4.8.0\"}");
        var r = Run(new[] { "--host", "claude-code" });
        Assert.Equal(FirstSeenLine + Environment.NewLine, r.Out);
        Assert.True(File.Exists(SeenPath("claude-code")));
    }

    [Fact]
    public void FreshDirectStamp_AndHermesHost_DoNotSuppress()
    {
        WriteNotes();
        PutState("direct-claude-code.json",
            "{\"at\":\"" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") + "\"}");
        var r = Run(new[] { "--host", "claude-code" });
        Assert.Equal(FirstSeenLine + Environment.NewLine, r.Out);

        var h = Run(new[] { "--host", "hermes" });
        Assert.Equal(FirstSeenLine + Environment.NewLine, h.Out);
        Assert.True(File.Exists(SeenPath("hermes")));
    }

    [Fact]
    public void JsonOutput_EscapesQuotesBackslashesAndControlCharacters()
    {
        // The summary is sanitised (control characters are removed by the engine), so quotes
        // and backslashes are what survive to the JSON writer.
        WriteNotes(summary: "Use \"quotes\" and C:\\path\\x \t tab.");
        var r = Run(new[] { "--host", "pi", "--format", "json" });
        using var doc = JsonDocument.Parse(r.Out);
        var text = doc.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("\"quotes\"", text);
        Assert.Contains("C:\\path\\x", text);

        var cc = Run(new[] { "--host", "cursor", "--format", "claude-code" });
        using var doc2 = JsonDocument.Parse(cc.Out);
        Assert.Equal(text, doc2.RootElement.GetProperty("systemMessage").GetString());
    }

    // ---- available notice (update check) ----

    private (int Exit, string Out) RunChecked(string[] args, string version = "4.8.0", string? env = null) =>
        Run(args, version, null, null, () => true, env);

    [Fact]
    public void Available_Text_PrintsLineAndMarksAvailableSeen()
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.8.0\"}");
        PutLatestStable();
        var r = RunChecked(new[] { "--host", "pi" });
        Assert.Equal(AvailableLine + Environment.NewLine, r.Out);
        Assert.Equal("{\"version\":\"4.8.0\",\"availableSeen\":{\"stable\":\"4.9.0\"}}",
            File.ReadAllText(SeenPath("pi")));
    }

    [Fact]
    public void Available_Json_KindAvailable()
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.8.0\"}");
        PutLatestStable();
        var r = RunChecked(new[] { "--host", "pi", "--format", "json" });
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal("available", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(AvailableLine, doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void Available_ClaudeCode_SystemMessage()
    {
        WriteNotes();
        PutState("seen-claude-code.json", "{\"version\":\"4.8.0\"}");
        PutLatestStable();
        var r = RunChecked(new[] { "--host", "claude-code", "--format", "claude-code" });
        using var doc = JsonDocument.Parse(r.Out);
        Assert.EndsWith(
            "Update: /total-recall:commands update More: https://github.com/strvmarv/total-recall/releases/tag/v4.9.0",
            doc.RootElement.GetProperty("systemMessage").GetString());
    }

    [Fact]
    public void Available_NoMark_WritesNothing()
    {
        WriteNotes();
        const string seenJson = "{\"version\":\"4.8.0\"}";
        PutState("seen-pi.json", seenJson);
        PutLatestStable();
        var a = RunChecked(new[] { "--host", "pi", "--no-mark" });
        var b = RunChecked(new[] { "--host", "pi", "--no-mark" });
        Assert.Equal(AvailableLine + Environment.NewLine, a.Out);
        Assert.Equal(a.Out, b.Out);
        Assert.Equal(seenJson, File.ReadAllText(SeenPath("pi")));
    }

    [Fact]
    public void Available_SameReleaseAgain_PrintsNothing()
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.8.0\"}");
        PutLatestStable();
        Assert.Equal(AvailableLine + Environment.NewLine, RunChecked(new[] { "--host", "pi" }).Out);
        Assert.Equal("", RunChecked(new[] { "--host", "pi" }).Out);
    }

    [Fact]
    public void Available_EvenNewer_PrintsAgain()
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.8.0\"}");
        PutLatestStable();
        RunChecked(new[] { "--host", "pi" });
        PutLatestStable("4.10.0", "Newer.");
        var r = RunChecked(new[] { "--host", "pi" });
        Assert.Equal(
            "total-recall 4.10.0 is available (you have 4.8.0): Newer. Update: pi update "
                + "More: https://github.com/strvmarv/total-recall/releases/tag/v4.10.0" + Environment.NewLine,
            r.Out);
    }

    [Fact]
    public void Order_UpdatedThenAvailableOnNextRun()
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.7.0\"}");
        PutLatestStable();
        Assert.Equal(UpdatedLine + Environment.NewLine, RunChecked(new[] { "--host", "pi" }).Out);
        Assert.Equal(AvailableLine + Environment.NewLine, RunChecked(new[] { "--host", "pi" }).Out);
        Assert.Equal("", RunChecked(new[] { "--host", "pi" }).Out);
    }

    [Fact]
    public void FirstSeen_ThenAvailableNextRun()
    {
        WriteNotes();
        PutLatestStable();
        Assert.Equal(FirstSeenLine + Environment.NewLine, RunChecked(new[] { "--host", "pi" }).Out);
        Assert.Equal(AvailableLine + Environment.NewLine, RunChecked(new[] { "--host", "pi" }).Out);
    }

    [Fact]
    public void MidSessionLatestFile_ReachesNextRun()
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.8.0\"}");
        Assert.Equal("", RunChecked(new[] { "--host", "pi" }).Out);
        PutLatestStable();
        Assert.Equal(AvailableLine + Environment.NewLine, RunChecked(new[] { "--host", "pi" }).Out);
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, "1", true)]
    [InlineData(true, null, false)]
    public void CheckOff_OldLatestFile_NothingAndNoMark(bool checkSetting, string? env, bool noticeEnabled)
    {
        WriteNotes();
        const string seenJson = "{\"version\":\"4.8.0\"}";
        PutState("seen-pi.json", seenJson);
        PutLatestStable();
        var r = Run(new[] { "--host", "pi" }, "4.8.0", () => noticeEnabled, null, () => checkSetting, env);
        Assert.Equal("", r.Out);
        Assert.Equal(seenJson, File.ReadAllText(SeenPath("pi")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    public void EnvEmptyZeroOrFalse_SettingDecides(string env)
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.8.0\"}");
        PutLatestStable();
        var r = RunChecked(new[] { "--host", "pi" }, env: env);
        Assert.Equal(AvailableLine + Environment.NewLine, r.Out);
    }

    [Fact]
    public void CheckSettingOmitted_NoAvailable()
    {
        WriteNotes();
        PutState("seen-pi.json", "{\"version\":\"4.8.0\"}");
        PutLatestStable();
        var r = Run(new[] { "--host", "pi" });
        Assert.Equal("", r.Out);
    }

    [Theory]
    [InlineData("claude-code", "claude-code", "/total-recall:commands update")]
    [InlineData("pi", "pi", "pi update")]
    [InlineData("cursor", "cursor", "see docs/hosts/cursor.md")]
    [InlineData("nonsense", "other", "see docs/hosts/other-mcp.md")]
    public void Available_HostInstruction_PerHost(string hostArg, string hostId, string how)
    {
        WriteNotes();
        PutState("seen-" + hostId + ".json", "{\"version\":\"4.8.0\"}");
        PutLatestStable();
        var r = RunChecked(new[] { "--host", hostArg });
        Assert.Equal(
            "total-recall 4.9.0 is available (you have 4.8.0): Faster sync. Update: " + how
                + " More: https://github.com/strvmarv/total-recall/releases/tag/v4.9.0" + Environment.NewLine,
            r.Out);
        Assert.Equal("{\"version\":\"4.8.0\",\"availableSeen\":{\"stable\":\"4.9.0\"}}",
            File.ReadAllText(SeenPath(hostId)));
    }

    [Fact]
    public void PrereleaseRunning_ReadsLatestPre()
    {
        PutState("seen-pi.json", "{\"version\":\"4.8.0-beta.1\"}");
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"Stable.\"}");
        PutLatest("pre", "{\"version\":\"4.8.0-beta.2\",\"summary\":\"Beta.\"}");
        var r = RunChecked(new[] { "--host", "pi" }, version: "4.8.0-beta.1");
        Assert.Equal(
            "total-recall 4.8.0-beta.2 is available (you have 4.8.0-beta.1): Beta. Update: pi update "
                + "More: https://github.com/strvmarv/total-recall/releases/tag/v4.8.0-beta.2" + Environment.NewLine,
            r.Out);
        Assert.Equal("{\"version\":\"4.8.0-beta.1\",\"availableSeen\":{\"pre\":\"4.8.0-beta.2\"}}",
            File.ReadAllText(SeenPath("pi")));
    }

    [Fact]
    public void Available_ThrowingWriter_NothingMarked()
    {
        WriteNotes();
        const string seenJson = "{\"version\":\"4.8.0\"}";
        PutState("seen-pi.json", seenJson);
        PutLatestStable();
        var r = Run(new[] { "--host", "pi" }, "4.8.0", null, new ThrowingWriter(), () => true, null);
        Assert.Equal(0, r.Exit);
        Assert.Equal(seenJson, File.ReadAllText(SeenPath("pi")));
    }

    [Fact]
    public void CommandIdentity()
    {
        var cmd = new WhatsNewCommand();
        Assert.Equal("whats-new", cmd.Name);
        Assert.Null(cmd.Group);
        Assert.False(string.IsNullOrWhiteSpace(cmd.Description));
    }

    [Fact]
    public void Registry_ListsWhatsNewInHelp()
    {
        var original = Console.Out;
        var sw = new StringWriter();
        try
        {
            Console.SetOut(sw);
            Assert.Equal(0, CliApp.Run(new[] { "--help" }));
        }
        finally { Console.SetOut(original); }
        Assert.Contains("whats-new", sw.ToString());
    }
}
