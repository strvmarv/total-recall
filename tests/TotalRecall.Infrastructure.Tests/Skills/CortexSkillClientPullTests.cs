using System.Net;
using System.Text;
using System.Text.Json;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Skills;

/// <summary>
/// Tests for <see cref="CortexSkillClient.GetModifiedSinceAsync"/>.
/// Verifies URL construction (with/without ?since=) and JSON deserialization.
/// </summary>
public class CortexSkillClientPullTests
{
    private static (CortexSkillClient client, RecordingHandler handler) Create(RecordingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://cortex.test") };
        return (new CortexSkillClient(http), handler);
    }

    private static PluginSyncSkillDto MakeDto(string name) => new(
        Id: Guid.NewGuid(),
        Name: name,
        Description: null,
        Content: "# body",
        Scope: "global",
        ScopeId: "",
        Tags: Array.Empty<string>(),
        Source: "claude-code",
        IsOrphaned: false,
        Version: 1,
        CreatedAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task GetModifiedSinceAsync_WithSince_IncludesSinceQueryParam()
    {
        var handler = new RecordingHandler(content: "[]", status: HttpStatusCode.OK);
        var (client, _) = Create(handler);
        var since = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        await client.GetModifiedSinceAsync(since, CancellationToken.None);

        Assert.NotNull(handler.LastUrl);
        Assert.Contains("/api/plugin/sync/skills", handler.LastUrl);
        Assert.Contains("since=", handler.LastUrl);
    }

    [Fact]
    public async Task GetModifiedSinceAsync_WithoutSince_NoQueryString()
    {
        var handler = new RecordingHandler(content: "[]", status: HttpStatusCode.OK);
        var (client, _) = Create(handler);

        await client.GetModifiedSinceAsync(since: null, CancellationToken.None);

        Assert.NotNull(handler.LastUrl);
        Assert.Equal("/api/plugin/sync/skills", handler.LastUrl);
    }

    [Fact]
    public async Task GetModifiedSinceAsync_DeserializesResponse()
    {
        var dtos = new[] { MakeDto("git-push"), MakeDto("code-review") };
        var json = JsonSerializer.Serialize(dtos, SyncJsonContext.Default.PluginSyncSkillDtoArray);
        var handler = new RecordingHandler(content: json, status: HttpStatusCode.OK);
        var (client, _) = Create(handler);

        var result = await client.GetModifiedSinceAsync(since: null, CancellationToken.None);

        Assert.Equal(2, result.Length);
        Assert.Equal("git-push", result[0].Name);
        Assert.Equal("code-review", result[1].Name);
    }

    [Fact]
    public async Task GetModifiedSinceAsync_EmptyArray_ReturnsEmpty()
    {
        var handler = new RecordingHandler(content: "[]", status: HttpStatusCode.OK);
        var (client, _) = Create(handler);

        var result = await client.GetModifiedSinceAsync(since: null, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetModifiedSinceAsync_UsesGetMethod()
    {
        var handler = new RecordingHandler(content: "[]", status: HttpStatusCode.OK);
        var (client, _) = Create(handler);

        await client.GetModifiedSinceAsync(since: null, CancellationToken.None);

        Assert.Equal("GET", handler.LastMethod);
    }
}
