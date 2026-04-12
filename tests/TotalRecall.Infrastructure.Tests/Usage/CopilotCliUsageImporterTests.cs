using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Usage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Usage;

public sealed class CopilotCliUsageImporterTests : System.IDisposable
{
    private readonly string _root;

    public CopilotCliUsageImporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tr-copilot-usage-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "session-state"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CopyFixtureAsSession(string sessionId)
    {
        var dir = Path.Combine(_root, "session-state", sessionId);
        Directory.CreateDirectory(dir);
        var fixture = Path.Combine(System.AppContext.BaseDirectory, "Usage", "Fixtures", "copilot-cli-sample.jsonl");
        var dest = Path.Combine(dir, "events.jsonl");
        File.Copy(fixture, dest, overwrite: true);
        return dest;
    }

    private static async Task<List<UsageEvent>> Drain(CopilotCliUsageImporter imp, long sinceMs)
    {
        var list = new List<UsageEvent>();
        await foreach (var e in imp.ScanAsync(sinceMs, CancellationToken.None))
            list.Add(e);
        return list;
    }

    [Fact]
    public void Detect_NoSessionStateDir_ReturnsFalse()
    {
        var imp = new CopilotCliUsageImporter(copilotHome: "/nonexistent");
        Assert.False(imp.Detect());
    }

    [Fact]
    public void Detect_SessionStateDirExists_ReturnsTrue()
    {
        var imp = new CopilotCliUsageImporter(copilotHome: _root);
        Assert.True(imp.Detect());
    }

    [Fact]
    public async Task ScanAsync_EmitsOneEventPerAssistantMessage()
    {
        CopyFixtureAsSession("s-copilot-1");
        var imp = new CopilotCliUsageImporter(copilotHome: _root);

        var events = await Drain(imp, sinceMs: 0);

        // Three assistant.message events in the fixture
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task ScanAsync_FirstMessage_UsesInitialContextAndLastKnownModel()
    {
        CopyFixtureAsSession("s-copilot-1");
        var imp = new CopilotCliUsageImporter(copilotHome: _root);

        var events = await Drain(imp, sinceMs: 0);
        var m1 = events.First(e => e.HostEventId == "evt-am-1");

        Assert.Equal("copilot-cli", m1.Host);
        Assert.Equal("s-copilot-1", m1.SessionId);
        Assert.Equal("/Users/test/proj", m1.ProjectPath);
        Assert.Equal("test/proj", m1.ProjectRepo);
        Assert.Equal("main", m1.ProjectBranch);
        Assert.Equal("abc123", m1.ProjectCommit);
        Assert.Equal("claude-sonnet-4.6", m1.Model);
        Assert.Equal(146, m1.OutputTokens);
        Assert.Equal("REQ-1", m1.HostRequestId);
        Assert.Equal("i-1", m1.InteractionId);
        // Claude Code-specific fields are null
        Assert.Null(m1.InputTokens);
        Assert.Null(m1.CacheCreation5m);
        Assert.Null(m1.CacheCreation1h);
        Assert.Null(m1.CacheRead);
        Assert.Null(m1.ServiceTier);
    }

    [Fact]
    public async Task ScanAsync_AfterContextChange_ReattributesBranch()
    {
        CopyFixtureAsSession("s-copilot-1");
        var imp = new CopilotCliUsageImporter(copilotHome: _root);

        var events = await Drain(imp, sinceMs: 0);
        var m2 = events.First(e => e.HostEventId == "evt-am-2");

        Assert.Equal("feature/xyz", m2.ProjectBranch);
        Assert.Equal("def456", m2.ProjectCommit);
    }

    [Fact]
    public async Task ScanAsync_AssistantMessageMissingOutputTokens_EmitsWithNullOutput()
    {
        CopyFixtureAsSession("s-copilot-1");
        var imp = new CopilotCliUsageImporter(copilotHome: _root);

        var events = await Drain(imp, sinceMs: 0);
        var m3 = events.First(e => e.HostEventId == "evt-am-3");

        Assert.Null(m3.OutputTokens);
    }

    [Fact]
    public async Task ScanAsync_Watermark_SkipsOlderEvents()
    {
        CopyFixtureAsSession("s-copilot-1");
        var imp = new CopilotCliUsageImporter(copilotHome: _root);

        // Only keep events strictly after 22:01:10 — excludes m1, keeps m2 and m3
        var cutoff = new System.DateTimeOffset(2026, 4, 9, 22, 1, 10, System.TimeSpan.Zero).ToUnixTimeMilliseconds();
        var events = await Drain(imp, sinceMs: cutoff);

        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, e => e.HostEventId == "evt-am-1");
    }
}
