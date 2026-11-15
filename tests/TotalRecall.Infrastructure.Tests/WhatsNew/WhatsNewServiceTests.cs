using System;
using System.IO;
using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class WhatsNewServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tr-whatsnew-" + Guid.NewGuid().ToString("N"));

    private string State => Path.Combine(_root, "state");
    private string Bin => Path.Combine(_root, "bin");

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteNotes(string version, string? previous, string summary)
    {
        Directory.CreateDirectory(Bin);
        var prev = previous is null ? "null" : "\"" + previous + "\"";
        File.WriteAllText(Path.Combine(Bin, "release-notes.json"),
            "{\"version\":\"" + version + "\",\"previous\":" + prev + ",\"summary\":\"" + summary + "\"}");
    }

    private void PutState(string name, string content)
    {
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, name), content);
    }

    private string SeenPath(string host) => Path.Combine(State, "seen-" + host + ".json");

    private WhatsNewService Make(string version, bool enabled = true, string? envHost = null) =>
        new(version, State, Bin, () => enabled, () => Now, () => envHost);

    private void PutLatest(string channel, string json) => PutState("latest-" + channel + ".json", json);

    private WhatsNewService MakeChecking(string version, bool check = true, string? env = null, string? envHost = null) =>
        new(version, State, Bin, () => true, () => Now, () => envHost, () => check, () => env);

    private string ReadSeenFile(string host) => File.ReadAllText(SeenPath(host));

    [Fact]
    public void Check_FirstCall_IsFirstSeenAndWritesNothingUntilMarkSeen()
    {
        WriteNotes("4.8.0", "4.7.0", "Faster search.");
        var svc = Make("4.8.0+abc");

        var notice = svc.Check("claude-code");

        Assert.NotNull(notice);
        Assert.Equal("first-seen", notice!.Kind);
        Assert.Contains("total-recall 4.8.0:", notice.Text);
        Assert.Contains("Faster search.", notice.Text);
        Assert.Equal(WhatsNewRenderer.Relay, notice.Relay);
        Assert.False(File.Exists(SeenPath("claude-code")));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void MarkSeen_WritesBareRunningVersion()
    {
        WriteNotes("4.8.0", "4.7.0", "Faster search.");
        var notice = Make("4.8.0+abc").Check("claude-code");

        notice!.MarkSeen();

        Assert.Equal("{\"version\":\"4.8.0\"}", File.ReadAllText(SeenPath("claude-code")));
    }

    [Fact]
    public void MarkSeen_CalledTwice_StaysTheSame()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        var notice = Make("4.8.0").Check("pi");

        notice!.MarkSeen();
        notice.MarkSeen();

        Assert.Equal("{\"version\":\"4.8.0\"}", File.ReadAllText(SeenPath("pi")));
    }

    [Fact]
    public void Check_AfterMarkSeen_IsNull()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        var svc = Make("4.8.0");
        svc.Check("pi")!.MarkSeen();

        Assert.Null(svc.Check("pi"));
    }

    [Fact]
    public void Check_HigherThanSeen_IsUpdatedNotice()
    {
        WriteNotes("4.8.0", "4.7.0", "Faster search.");
        PutState("seen-pi.json", "{\"version\":\"4.7.0\"}");

        var notice = Make("4.8.0").Check("pi");

        Assert.NotNull(notice);
        Assert.Equal("updated", notice!.Kind);
        Assert.Contains("4.7.0 -> 4.8.0", notice.Text);
    }

    [Fact]
    public void Check_TwoHosts_AreIndependent()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        var svc = Make("4.8.0");
        svc.Check("pi")!.MarkSeen();

        Assert.Null(svc.Check("pi"));
        Assert.NotNull(svc.Check("cursor"));
        Assert.False(File.Exists(SeenPath("cursor")));
    }

    [Theory]
    [InlineData("4.6.0", "{\"version\":\"4.7.0\"}")]
    [InlineData("4.7.0", "{\"version\":\"4.7.0\"}")]
    [InlineData("1.0.0", "{\"version\":\"4.7.0\"}")]
    [InlineData("1.0.0", null)]
    [InlineData("unknown", null)]
    public void Check_LowerEqualOrDevBuild_IsNullAndRecordsNothing(string running, string? seenJson)
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        if (seenJson is not null) PutState("seen-pi.json", seenJson);
        var before = seenJson is null ? null : File.ReadAllBytes(SeenPath("pi"));

        Assert.Null(Make(running).Check("pi"));

        if (before is null) Assert.False(File.Exists(SeenPath("pi")));
        else Assert.Equal(before, File.ReadAllBytes(SeenPath("pi")));
    }

    [Fact]
    public void Check_NoticeDisabled_IsNullAndRecordsNothing()
    {
        WriteNotes("4.8.0", "4.7.0", "s");

        Assert.Null(Make("4.8.0", enabled: false).Check("pi"));

        Assert.False(File.Exists(SeenPath("pi")));
    }

    [Fact]
    public void Check_NoticeDisabled_LeavesExistingSeenFileUnchanged()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        PutState("seen-pi.json", "{\"version\":\"4.7.0\"}");
        var before = File.ReadAllBytes(SeenPath("pi"));

        Assert.Null(Make("4.8.0", enabled: false).Check("pi"));

        Assert.Equal(before, File.ReadAllBytes(SeenPath("pi")));
    }

    [Fact]
    public void Check_EnvHostHermes_IsNullAndWritesNothing()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        var svc = Make("4.8.0", envHost: "hermes");

        Assert.Null(svc.Check("pi"));
        Assert.Null(svc.Check(null));

        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void Check_HermesClientName_IsNull()
    {
        WriteNotes("4.8.0", "4.7.0", "s");

        Assert.Null(Make("4.8.0").Check("hermes-mcp-client"));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public void Check_EnvHostWinsOverClientName()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        var svc = Make("4.8.0", envHost: "cursor");

        svc.Check("claude-code")!.MarkSeen();

        Assert.True(File.Exists(SeenPath("cursor")));
        Assert.False(File.Exists(SeenPath("claude-code")));
    }

    [Fact]
    public void Check_FreshDirectStamp_IsNull()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        PutState("direct-claude-code.json", "{\"at\":\"" + Now.AddDays(-1).ToString("O") + "\"}");

        Assert.Null(Make("4.8.0", envHost: "claude-code").Check("anything"));
    }

    [Fact]
    public void Check_StaleDirectStamp_IsNotice()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        PutState("direct-claude-code.json", "{\"at\":\"" + Now.AddDays(-40).ToString("O") + "\"}");

        Assert.NotNull(Make("4.8.0", envHost: "claude-code").Check("anything"));
    }

    [Fact]
    public void Check_MissingNotesFile_StillGivesNoticeWithoutSummary()
    {
        var notice = Make("4.8.0").Check("pi");

        Assert.NotNull(notice);
        Assert.Equal("first-seen", notice!.Kind);
    }

    [Fact]
    public void Construction_WithMissingDirs_CreatesNothingAndDoesNotThrow()
    {
        var svc = Make("4.8.0");

        Assert.NotNull(svc);
        Assert.False(Directory.Exists(State));
        Assert.False(Directory.Exists(Bin));
    }

    [Fact]
    public void Check_StateDirIsAFile_NeverThrows()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(State, "not a directory");
        WriteNotes("4.8.0", "4.7.0", "s");

        var notice = Make("4.8.0").Check("pi");

        Assert.NotNull(notice);
        notice!.MarkSeen();
        Assert.True(File.Exists(State));
    }

    [Fact]
    public void Check_ThrowingDelegate_ReturnsNull()
    {
        var svc = new WhatsNewService("4.8.0", State, Bin, () => throw new InvalidOperationException(), () => Now, () => null);

        Assert.Null(svc.Check("pi"));
    }

    private const string Seen48 = "{\"version\":\"4.8.0\"}";

    [Fact]
    public void Check_SeenEqualsRunning_NewerLatest_IsAvailable_MarkSeenWritesAvailableSeen()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"Faster sync.\"}");

        var notice = MakeChecking("4.8.0").Check("claude-code");

        Assert.NotNull(notice);
        Assert.Equal("available", notice!.Kind);
        Assert.Equal(
            "total-recall 4.9.0 is available (you have 4.8.0): Faster sync. Update: /total-recall:commands update More: https://github.com/strvmarv/total-recall/releases/tag/v4.9.0",
            notice.Text);
        Assert.Equal(Seen48, ReadSeenFile("claude-code"));

        notice.MarkSeen();

        Assert.Equal("{\"version\":\"4.8.0\",\"availableSeen\":{\"stable\":\"4.9.0\"}}", ReadSeenFile("claude-code"));
    }

    [Fact]
    public void Check_AvailableMarked_NewInstance_IsNull()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");
        MakeChecking("4.8.0").Check("claude-code")!.MarkSeen();

        Assert.Null(MakeChecking("4.8.0").Check("claude-code"));
    }

    [Fact]
    public void Check_EvenNewerRelease_NewInstance_IsAvailableAgain()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");
        MakeChecking("4.8.0").Check("claude-code")!.MarkSeen();
        PutLatest("stable", "{\"version\":\"4.10.0\",\"summary\":\"s\"}");

        var notice = MakeChecking("4.8.0").Check("claude-code");

        Assert.NotNull(notice);
        Assert.Equal("available", notice!.Kind);
        Assert.Contains("total-recall 4.10.0 is available", notice.Text);
    }

    [Fact]
    public void Check_UpdatedFirst_ThenAvailableOnNextEngineStart()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        PutState("seen-claude-code.json", "{\"version\":\"4.7.0\"}");
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");

        var one = MakeChecking("4.8.0");
        var first = one.Check("claude-code");
        Assert.Equal("updated", first!.Kind);
        first.MarkSeen();
        Assert.Null(one.Check("claude-code"));

        var two = MakeChecking("4.8.0");
        var second = two.Check("claude-code");
        Assert.Equal("available", second!.Kind);
        second.MarkSeen();

        Assert.Null(MakeChecking("4.8.0").Check("claude-code"));
        Assert.Equal("{\"version\":\"4.8.0\",\"availableSeen\":{\"stable\":\"4.9.0\"}}", ReadSeenFile("claude-code"));
    }

    [Fact]
    public void Check_SecondCallSameInstance_AfterMark_IsNull_EvenWithAvailableDue()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");
        var svc = MakeChecking("4.8.0");

        var first = svc.Check("claude-code");
        Assert.Equal("first-seen", first!.Kind);
        first.MarkSeen();

        Assert.Null(svc.Check("claude-code"));
    }

    [Fact]
    public void Check_NotMarked_SecondCallStillShows()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");
        var svc = MakeChecking("4.8.0");

        var first = svc.Check("claude-code");
        var second = svc.Check("claude-code");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Text, second!.Text);
        Assert.Equal(Seen48, ReadSeenFile("claude-code"));
    }

    [Fact]
    public void Check_MarkedHost_DoesNotSilenceOtherHosts()
    {
        WriteNotes("4.8.0", "4.7.0", "s");
        var svc = MakeChecking("4.8.0");
        svc.Check("pi")!.MarkSeen();

        Assert.Null(svc.Check("pi"));
        Assert.NotNull(svc.Check("cursor"));
    }

    [Fact]
    public void Check_CheckSettingOff_OldLatestFile_NoAvailable()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");

        Assert.Null(MakeChecking("4.8.0", check: false).Check("claude-code"));
    }

    [Fact]
    public void Check_CheckSettingOmitted_NoAvailable()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");

        Assert.Null(Make("4.8.0").Check("claude-code"));
    }

    [Fact]
    public void Check_NoticeOff_NoAvailable()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");
        var svc = new WhatsNewService("4.8.0", State, Bin, () => false, () => Now, () => null, () => true, () => null);

        Assert.Null(svc.Check("claude-code"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData(" TRUE ")]
    public void Check_EnvDisables_OldLatestFile_NoAvailable(string env)
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");

        Assert.Null(MakeChecking("4.8.0", env: env).Check("claude-code"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void Check_EnvEmptyZeroOrFalse_SettingDecides(string? env)
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");

        var notice = MakeChecking("4.8.0", env: env).Check("claude-code");

        Assert.NotNull(notice);
        Assert.Equal("available", notice!.Kind);
    }

    [Fact]
    public void Check_Hermes_NoAvailable()
    {
        PutState("seen-hermes.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");

        Assert.Null(MakeChecking("4.8.0", envHost: "hermes").Check("pi"));
        Assert.Null(MakeChecking("4.8.0").Check("hermes-mcp-client"));
    }

    [Fact]
    public void Check_FreshDirectStamp_NoAvailable()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");
        PutState("direct-claude-code.json", "{\"at\":\"" + Now.AddDays(-1).ToString("O") + "\"}");

        Assert.Null(MakeChecking("4.8.0", envHost: "claude-code").Check("anything"));
    }

    [Fact]
    public void Check_PrereleaseEngine_ReadsLatestPre()
    {
        PutState("seen-claude-code.json", "{\"version\":\"4.8.0-beta.1\"}");
        PutLatest("stable", "{\"version\":\"9.9.9\",\"summary\":\"s\"}");

        Assert.Null(MakeChecking("4.8.0-beta.1").Check("claude-code"));

        PutLatest("pre", "{\"version\":\"4.8.0-beta.2\",\"summary\":\"s\"}");
        var notice = MakeChecking("4.8.0-beta.1").Check("claude-code");

        Assert.NotNull(notice);
        Assert.Equal("available", notice!.Kind);
        Assert.Contains("total-recall 4.8.0-beta.2 is available (you have 4.8.0-beta.1)", notice.Text);
        notice.MarkSeen();
        Assert.Equal("{\"version\":\"4.8.0-beta.1\",\"availableSeen\":{\"pre\":\"4.8.0-beta.2\"}}", ReadSeenFile("claude-code"));
    }

    [Fact]
    public void Check_StableAndBetaEnginesShareHost_AvailableSeenPerChannel()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");
        PutLatest("pre", "{\"version\":\"4.10.0-beta.1\",\"summary\":\"s\"}");

        var stable = MakeChecking("4.8.0").Check("claude-code");
        Assert.Equal("available", stable!.Kind);
        stable.MarkSeen();

        var beta = MakeChecking("4.8.0-beta.1").Check("claude-code");
        Assert.Equal("available", beta!.Kind);
        beta.MarkSeen();

        var json = ReadSeenFile("claude-code");
        Assert.Contains("\"stable\":\"4.9.0\"", json);
        Assert.Contains("\"pre\":\"4.10.0-beta.1\"", json);
        Assert.Null(MakeChecking("4.8.0").Check("claude-code"));
        Assert.Null(MakeChecking("4.8.0-beta.1").Check("claude-code"));
    }

    [Fact]
    public void Check_LatestFileWithHostileSummary_TextIsSanitised_LinkIsReleasePage()
    {
        PutState("seen-claude-code.json", Seen48);
        PutLatest("stable",
            "{\"version\":\"4.9.0\",\"summary\":\"Click [here](https://evil.example/x)\\u0007 now\"}");

        var notice = MakeChecking("4.8.0").Check("claude-code");

        Assert.NotNull(notice);
        Assert.DoesNotContain("evil.example", notice!.Text);
        Assert.False(notice.Text.Contains('\u0007'));
        Assert.Contains("Click here now Update:", notice.Text);
        Assert.True(notice.Text.Length <= 1000);
        Assert.EndsWith("More: https://github.com/strvmarv/total-recall/releases/tag/v4.9.0", notice.Text);
    }

    [Fact]
    public void Check_Available_UsesResolvedHostInstruction()
    {
        PutState("seen-pi.json", Seen48);
        PutLatest("stable", "{\"version\":\"4.9.0\",\"summary\":\"s\"}");

        var notice = MakeChecking("4.8.0", envHost: "pi").Check("claude-code");

        Assert.NotNull(notice);
        Assert.Contains("Update: pi update", notice!.Text);
    }
}
