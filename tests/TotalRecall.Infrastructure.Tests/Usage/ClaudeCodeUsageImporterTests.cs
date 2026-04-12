using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Usage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Usage;

public sealed class ClaudeCodeUsageImporterTests : System.IDisposable
{
    private readonly string _projectsDir;

    public ClaudeCodeUsageImporterTests()
    {
        _projectsDir = Path.Combine(Path.GetTempPath(), "tr-cc-usage-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectsDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_projectsDir)) Directory.Delete(_projectsDir, recursive: true); } catch { }
    }

    private string CopyFixtureAsSession(string sessionId, string projectEncodedCwd)
    {
        var projectDir = Path.Combine(_projectsDir, projectEncodedCwd);
        Directory.CreateDirectory(projectDir);
        var fixture = Path.Combine(System.AppContext.BaseDirectory, "Usage", "Fixtures", "claude-code-sample.jsonl");
        var dest = Path.Combine(projectDir, sessionId + ".jsonl");
        File.Copy(fixture, dest, overwrite: true);
        return dest;
    }

    private static async Task<List<UsageEvent>> Drain(ClaudeCodeUsageImporter imp, long sinceMs)
    {
        var list = new List<UsageEvent>();
        await foreach (var e in imp.ScanAsync(sinceMs, CancellationToken.None))
            list.Add(e);
        return list;
    }

    [Fact]
    public void Detect_NoProjectsDir_ReturnsFalse()
    {
        var imp = new ClaudeCodeUsageImporter(projectsDir: "/nonexistent/path");
        Assert.False(imp.Detect());
    }

    [Fact]
    public void Detect_ExistingDir_ReturnsTrue()
    {
        var imp = new ClaudeCodeUsageImporter(_projectsDir);
        Assert.True(imp.Detect());
    }

    [Fact]
    public async Task ScanAsync_FixtureTranscript_YieldsTwoEvents()
    {
        CopyFixtureAsSession("abc-session", "-Users-test-project");
        var imp = new ClaudeCodeUsageImporter(_projectsDir);

        var events = await Drain(imp, sinceMs: 0);

        // Only rec-2 and rec-3 emit events:
        //  - rec-1 is a user message (no usage)
        //  - malformed line skipped
        //  - rec-4 has no usage
        //  - rec-5 has usage but model="<synthetic>" — filtered out
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task ScanAsync_FullUsageRecord_PopulatesAllFields()
    {
        CopyFixtureAsSession("abc-session", "-Users-test-project");
        var imp = new ClaudeCodeUsageImporter(_projectsDir);

        var events = await Drain(imp, sinceMs: 0);
        var rec2 = events.First(e => e.HostEventId == "rec-2");

        Assert.Equal("claude-code", rec2.Host);
        Assert.Equal("abc-session", rec2.SessionId);
        Assert.Equal("claude-opus-4.1", rec2.Model);
        Assert.Equal(100, rec2.InputTokens);
        Assert.Equal(300, rec2.CacheCreation5m);
        Assert.Equal(200, rec2.CacheCreation1h);
        Assert.Equal(200, rec2.CacheRead);
        Assert.Equal(50, rec2.OutputTokens);
        Assert.Equal("standard", rec2.ServiceTier);
        Assert.NotNull(rec2.ServerToolUseJson);
        Assert.Contains("web_search", rec2.ServerToolUseJson!);
        Assert.Null(rec2.ProjectRepo);   // Claude Code never populates this
        Assert.Equal("feature/x", rec2.ProjectBranch);   // taken from top-level gitBranch on the record
        Assert.Equal(0, rec2.TurnIndex);   // first assistant turn with usage → index 0

        var rec3 = events.First(e => e.HostEventId == "rec-3");
        Assert.Equal(1, rec3.TurnIndex);   // second assistant turn with usage → index 1
    }

    [Fact]
    public async Task ScanAsync_MinimalUsageRecord_LeavesCacheFieldsNull()
    {
        CopyFixtureAsSession("abc-session", "-Users-test-project");
        var imp = new ClaudeCodeUsageImporter(_projectsDir);

        var events = await Drain(imp, sinceMs: 0);
        var rec3 = events.First(e => e.HostEventId == "rec-3");

        Assert.Equal(75, rec3.InputTokens);
        Assert.Equal(20, rec3.OutputTokens);
        Assert.Null(rec3.CacheCreation5m);
        Assert.Null(rec3.CacheCreation1h);
        Assert.Null(rec3.CacheRead);
    }

    [Fact]
    public async Task ScanAsync_Watermark_SkipsOlderEvents()
    {
        CopyFixtureAsSession("abc-session", "-Users-test-project");
        var imp = new ClaudeCodeUsageImporter(_projectsDir);

        // rec-2 at 10:00:05, rec-3 at 10:00:10. Watermark at 10:00:07
        // excludes rec-2 and keeps rec-3.
        var cutoff = new System.DateTimeOffset(2026, 4, 9, 10, 0, 7, System.TimeSpan.Zero).ToUnixTimeMilliseconds();
        var events = await Drain(imp, sinceMs: cutoff);

        Assert.Single(events);
        Assert.Equal("rec-3", events[0].HostEventId);
    }

    [Fact]
    public async Task ScanAsync_EmptyDir_YieldsNothing()
    {
        var imp = new ClaudeCodeUsageImporter(_projectsDir);
        var events = await Drain(imp, sinceMs: 0);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ScanAsync_ProjectPath_PrefersRecordCwdOverEncodedDirName()
    {
        // Bug 3 regression: the authoritative project path comes from the
        // top-level cwd field on records, NOT the reverse-mapped encoded
        // directory name (which corrupts real hyphens). Even if the encoded
        // dir name says "totally-different", the events should report
        // whatever cwd the records themselves carry.
        CopyFixtureAsSession("s1", "-Users-totally-different-path");
        var imp = new ClaudeCodeUsageImporter(_projectsDir);

        var events = await Drain(imp, sinceMs: 0);

        Assert.NotEmpty(events);
        // All emitted events must use the cwd carried by the records
        // (set on rec-1/rec-2 in the fixture), preserving real hyphens.
        foreach (var e in events)
            Assert.Equal("/Users/real/total-recall", e.ProjectPath);
    }

    [Fact]
    public async Task ScanAsync_ProjectPath_FallsBackToDecodedDirName_WhenNoCwdOnRecords()
    {
        // Fallback: if no record surfaces a cwd, the decoded dir name
        // is used. Write a minimal transcript here (single assistant
        // record with usage but no cwd/gitBranch top-level fields).
        var projectDir = Path.Combine(_projectsDir, "-Users-strvmarv-source-total--recall");
        Directory.CreateDirectory(projectDir);
        var jsonl = Path.Combine(projectDir, "fallback.jsonl");
        File.WriteAllText(jsonl,
            "{\"uuid\":\"r1\",\"timestamp\":\"2026-04-09T10:00:05.000Z\",\"sessionId\":\"s1\",\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"model\":\"claude-opus-4.1\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}\n");

        var imp = new ClaudeCodeUsageImporter(_projectsDir);
        var events = await Drain(imp, sinceMs: 0);

        Assert.Single(events);
        // Decoded: "-Users-strvmarv-source-total--recall"
        //   single hyphens → slashes, "--" → "-"
        Assert.Equal("/Users/strvmarv/source/total-recall", events[0].ProjectPath);
    }

    [Fact]
    public async Task ScanAsync_SyntheticModelRecord_IsSkipped()
    {
        // Bug 2 regression: records with model="<synthetic>" are Claude Code
        // internal protocol markers and must not contribute to usage
        // aggregates, even though they carry a valid usage block.
        CopyFixtureAsSession("abc-session", "-Users-test-project");
        var imp = new ClaudeCodeUsageImporter(_projectsDir);

        var events = await Drain(imp, sinceMs: 0);

        Assert.DoesNotContain(events, e => e.Model == "<synthetic>");
        Assert.DoesNotContain(events, e => e.HostEventId == "rec-5");
    }
}
