// tests/TotalRecall.Server.Tests/SessionStartHandlerTests.cs
//
// Plan 4 Task 4.10 — unit tests for SessionStartHandler. Uses the
// FakeSessionLifecycle in TestSupport/ to avoid wiring the full
// SessionLifecycle + importer + store + compaction-log graph.

namespace TotalRecall.Server.Tests;

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class SessionStartHandlerTests
{
    private static SessionInitResult CannedResult() => new(
        SessionId: "sess-42",
        Project: "demo",
        ImportSummary: new List<ImportSummaryRow>
        {
            new("claude-code", 2, 1, 0, 0, 0, 0, System.Array.Empty<string>()),
        },
        WarmSweep: null,
        WarmPromoted: 0,
        ProjectDocs: null,
        HotEntryCount: 3,
        Context: "- hello",
        TierSummary: new TierSummary(3, 4, 5, 6, 7),
        Hints: new List<string> { "hint1", "hint2" },
        LastSessionAge: "5 minutes ago",
        SmokeTest: null,
        RegressionAlerts: null,
        Storage: "sqlite");

    [Fact]
    public async Task HappyPath_ReturnsSessionInitResult()
    {
        var lifecycle = new FakeSessionLifecycle
        {
            SessionIdValue = "sess-42",
            ResultToReturn = CannedResult(),
        };
        var handler = new SessionStartHandler(lifecycle);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError ?? false);
        Assert.Single(result.Content);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal("sess-42", root.GetProperty("sessionId").GetString());
        Assert.Equal("demo", root.GetProperty("project").GetString());
        Assert.Equal(3, root.GetProperty("hotEntryCount").GetInt32());
        Assert.Equal("- hello", root.GetProperty("context").GetString());

        var hints = root.GetProperty("hints");
        Assert.Equal(JsonValueKind.Array, hints.ValueKind);
        Assert.Equal(2, hints.GetArrayLength());

        var summary = root.GetProperty("tierSummary");
        Assert.Equal(3, summary.GetProperty("hot").GetInt32());
        Assert.Equal(4, summary.GetProperty("warm").GetInt32());
        Assert.Equal(5, summary.GetProperty("cold").GetInt32());
    }

    [Fact]
    public async Task NullArguments_DoesNotThrow()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionStartHandler(lifecycle);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, lifecycle.EnsureInitializedCallCount);
    }

    [Fact]
    public async Task EmptyObjectArguments_DoesNotThrow()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionStartHandler(lifecycle);

        using var doc = JsonDocument.Parse("{}");
        var result = await handler.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, lifecycle.EnsureInitializedCallCount);
    }

    [Fact]
    public async Task CallsEnsureInitializedAsync_Once()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionStartHandler(lifecycle);

        await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.Equal(1, lifecycle.EnsureInitializedCallCount);
    }

    [Fact]
    public async Task CallsEnsureInitializedAsync_WithToken()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionStartHandler(lifecycle);
        using var cts = new CancellationTokenSource();

        await handler.ExecuteAsync(null, cts.Token);

        Assert.Equal(cts.Token, lifecycle.LastToken);
    }

    [Fact]
    public async Task JsonShape_UsesExplicitCamelCaseFieldNames()
    {
        var lifecycle = new FakeSessionLifecycle
        {
            ResultToReturn = CannedResult(),
        };
        var handler = new SessionStartHandler(lifecycle);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        // Matches the [JsonPropertyName] values on SessionInitResult —
        // camelCase on the wire (e.g. hotEntryCount, not hot_entry_count).
        var text = result.Content[0].Text;
        Assert.Contains("\"sessionId\"", text);
        Assert.Contains("\"importSummary\"", text);
        Assert.Contains("\"hotEntryCount\"", text);
        Assert.Contains("\"tierSummary\"", text);
        Assert.Contains("\"lastSessionAge\"", text);
    }

    [Fact]
    public void Metadata_NameAndDescription()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionStartHandler(lifecycle);

        Assert.Equal("session_start", handler.Name);
        Assert.Contains("Initialize", handler.Description);
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
    }
}
