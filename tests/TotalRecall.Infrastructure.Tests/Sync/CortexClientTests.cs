using System.Net;
using System.Text;
using System.Text.Json;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

/// <summary>
/// Minimal HTTP message handler that captures the last request and returns
/// a pre-configured response. Lives in the test file, not a shared utility.
/// </summary>
internal sealed class MockHttpHandler : HttpMessageHandler
{
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public string ResponseBody { get; set; } = "{}";
    public HttpRequestMessage? LastRequest { get; private set; }
    public bool ShouldThrow { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;

        if (ShouldThrow)
            throw new HttpRequestException("Simulated network failure");

        var response = new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

public class CortexClientTests
{
    private static (CortexClient client, MockHttpHandler handler) CreateClient()
    {
        var handler = new MockHttpHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cortex.test") };
        return (new CortexClient(http), handler);
    }

    [Fact]
    public async Task UpsertMemoriesAsync_Posts_To_Correct_Endpoint()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = "{}";

        var entry = new SyncEntry(
            "id-1", "hello", "fact", "text", Array.Empty<string>(),
            null, 0, 1.0, DateTime.UtcNow, DateTime.UtcNow);

        await client.UpsertMemoriesAsync(new[] { entry }, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/plugin/sync/memories", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_Gets_From_Correct_Endpoint_And_Deserializes()
    {
        var (client, handler) = CreateClient();
        var results = new[]
        {
            new SyncSearchResult("id-1", "content", 0.95, null, null),
            new SyncSearchResult("id-2", "other", 0.80, "src", new[] { "tag" })
        };
        handler.ResponseBody = JsonSerializer.Serialize(results);

        var actual = await client.SearchKnowledgeAsync("test query", 5, null, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.StartsWith("/api/plugin/sync/knowledge", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("query=test%20query", handler.LastRequest.RequestUri.Query);
        Assert.Contains("top_k=5", handler.LastRequest.RequestUri.Query);
        Assert.DoesNotContain("scopes=", handler.LastRequest.RequestUri.Query);
        Assert.Equal(2, actual.Length);
        Assert.Equal("id-1", actual[0].Id);
        Assert.Equal(0.95, actual[0].Score);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_Forwards_Scopes_As_Comma_Separated_Query_Param()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = JsonSerializer.Serialize(Array.Empty<SyncSearchResult>());

        await client.SearchKnowledgeAsync(
            "q", 10,
            new[] { "user:paul", "global:jira" },
            CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("scopes=user%3Apaul%2Cglobal%3Ajira", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Network_Failure_Throws_CortexUnreachableException()
    {
        var (client, handler) = CreateClient();
        handler.ShouldThrow = true;

        await Assert.ThrowsAsync<CortexUnreachableException>(
            () => client.GetStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetUserMemoriesModifiedSinceAsync_Parses_Pull_Response_With_Tombstones()
    {
        var (client, handler) = CreateClient();
        var now = DateTime.UtcNow;
        var pullResult = new SyncPullResult(
            new[]
            {
                new SyncEntry("id-1", "alive", "fact", "text", Array.Empty<string>(),
                    null, 1, 0.9, now, now),
                new SyncEntry("id-2", "deleted", "fact", "text", Array.Empty<string>(),
                    null, 0, 0.0, now, now, DeletedAt: now)
            },
            TombstoneHorizon: now.AddDays(-30));

        handler.ResponseBody = JsonSerializer.Serialize(pullResult);

        var since = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = await client.GetUserMemoriesModifiedSinceAsync(since, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.StartsWith("/api/plugin/sync/memories", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("since=", handler.LastRequest.RequestUri.Query);

        Assert.Equal(2, result.Memories.Length);
        Assert.Null(result.Memories[0].DeletedAt);
        Assert.NotNull(result.Memories[1].DeletedAt);
        Assert.NotNull(result.TombstoneHorizon);
    }
}
