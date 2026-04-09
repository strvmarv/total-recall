// Plan 6 Task 6.0d — CompactNowHandler contract tests.
//
// The handler is an informational stub — it mirrors the CLI
// CompactCommand stub and always returns {compacted: 0, message: ...}.
// These tests pin the shape so future refactors notice breakage.

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class CompactNowHandlerTests
{
    [Fact]
    public async Task HappyPath_ReturnsStubShape()
    {
        var handler = new CompactNowHandler();
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(0, doc.RootElement.GetProperty("compacted").GetInt32());
        var msg = doc.RootElement.GetProperty("message").GetString();
        Assert.NotNull(msg);
        Assert.Contains("host-orchestrated", msg!);
    }

    [Fact]
    public async Task EmptyArgs_NoError()
    {
        var handler = new CompactNowHandler();
        var args = JsonDocument.Parse("{}").RootElement.Clone();
        var result = await handler.ExecuteAsync(args, CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotEmpty(result.Content);
    }

    [Fact]
    public async Task JsonRoundtrip_DtoShape()
    {
        var handler = new CompactNowHandler();
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.CompactNowResultDto);
        Assert.NotNull(dto);
        Assert.Equal(0, dto!.Compacted);
        Assert.False(string.IsNullOrEmpty(dto.Message));
    }

    [Fact]
    public void Name_Is_compact_now()
    {
        var handler = new CompactNowHandler();
        Assert.Equal("compact_now", handler.Name);
    }
}
