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
}
