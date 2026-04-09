// Plan 6 Task 6.0c — EvalSnapshotHandler contract tests.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class EvalSnapshotHandlerTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task HappyPath_ReturnsIdAndName()
    {
        var handler = new EvalSnapshotHandler(name => ("snap-1", false));
        var result = await handler.ExecuteAsync(Args("""{"name":"v1"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("snap-1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("v1", doc.RootElement.GetProperty("name").GetString());
        Assert.False(doc.RootElement.GetProperty("deduped").GetBoolean());
    }

    [Fact]
    public async Task Deduped_ReportsFlag()
    {
        var handler = new EvalSnapshotHandler(name => ("snap-1", true));
        var result = await handler.ExecuteAsync(Args("""{"name":"v1"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.True(doc.RootElement.GetProperty("deduped").GetBoolean());
    }

    [Fact]
    public async Task MissingName_Throws()
    {
        var handler = new EvalSnapshotHandler(name => ("x", false));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyName_Throws()
    {
        var handler = new EvalSnapshotHandler(name => ("x", false));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"name":""}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var handler = new EvalSnapshotHandler(name => ("snap-1", false));
        var result = await handler.ExecuteAsync(Args("""{"name":"v1"}"""), CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.EvalSnapshotResultDto);
        Assert.NotNull(dto);
        Assert.Equal("snap-1", dto!.Id);
        Assert.Equal("v1", dto.Name);
    }

    [Fact]
    public void Name_Is_eval_snapshot()
    {
        var handler = new EvalSnapshotHandler(name => ("x", false));
        Assert.Equal("eval_snapshot", handler.Name);
    }
}
