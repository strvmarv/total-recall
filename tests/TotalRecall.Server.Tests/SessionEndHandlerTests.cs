// tests/TotalRecall.Server.Tests/SessionEndHandlerTests.cs
//
// Plan 4 Task 4.10 — unit tests for SessionEndHandler. The handler is a
// Plan 4 stub (see the TODO(Plan 5+) marker on the handler); these tests
// lock in that stub shape so accidental drift shows up as a red test.

namespace TotalRecall.Server.Tests;

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class SessionEndHandlerTests
{
    [Fact]
    public async Task HappyPath_ReturnsStubResponse()
    {
        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "sess-end-1" };
        var handler = new SessionEndHandler(lifecycle);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError ?? false);
        Assert.Single(result.Content);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal("sess-end-1", root.GetProperty("sessionId").GetString());
        Assert.Equal(0, root.GetProperty("carryForward").GetInt32());
        Assert.Equal(0, root.GetProperty("promoted").GetInt32());
        Assert.Equal(0, root.GetProperty("discarded").GetInt32());
    }

    [Fact]
    public async Task NullArguments_DoesNotThrow()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionEndHandler(lifecycle);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task EmptyObjectArguments_DoesNotThrow()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionEndHandler(lifecycle);

        using var doc = JsonDocument.Parse("{}");
        var result = await handler.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task SessionId_FromLifecycle()
    {
        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "unique-id-xyz" };
        var handler = new SessionEndHandler(lifecycle);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("unique-id-xyz", doc.RootElement.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task DoesNotCall_EnsureInitializedAsync()
    {
        // session_end echoes SessionId directly; it must not force init.
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionEndHandler(lifecycle);

        await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.Equal(0, lifecycle.EnsureInitializedCallCount);
    }

    [Fact]
    public void Metadata_NameAndDescription()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionEndHandler(lifecycle);

        Assert.Equal("session_end", handler.Name);
        Assert.Contains("End a session", handler.Description);
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
    }
}
