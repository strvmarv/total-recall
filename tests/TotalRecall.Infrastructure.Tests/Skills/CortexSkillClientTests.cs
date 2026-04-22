using System.Net;
using System.Text;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Skills;

/// <summary>
/// Minimal HTTP message handler that captures the last request (method, url,
/// body) and returns a queued sequence of responses. Declared locally —
/// mirrors the pattern in <c>Sync/CortexClientTests.cs</c>.
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    public string? LastUrl { get; private set; }
    public string? LastMethod { get; private set; }
    public string? LastBody { get; private set; }
    public bool ShouldThrow { get; set; }

    public RecordingHandler(string content = "{}", HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((status, content));
    }

    public RecordingHandler Enqueue(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((status, content));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUrl = request.RequestUri?.PathAndQuery;
        LastMethod = request.Method.Method;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (ShouldThrow)
            throw new HttpRequestException("Simulated network failure");

        var (status, body) = _responses.Count > 0
            ? _responses.Dequeue()
            : (HttpStatusCode.OK, "{}");

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}

public class CortexSkillClientTests
{
    private static (CortexSkillClient client, RecordingHandler handler) Create(RecordingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://cortex.test") };
        return (new CortexSkillClient(http), handler);
    }

    [Fact]
    public async Task SearchAsync_HitsExpectedUrl_AndDeserialisesHits()
    {
        var handler = new RecordingHandler(
            content: """[{"id":"00000000-0000-0000-0000-000000000001","name":"foo","description":null,"scope":"user","scopeId":"user:u1","tags":[],"score":0.9,"excerpt":"e"}]""");
        var (client, _) = Create(handler);

        var hits = await client.SearchAsync("q", scope: null, tags: null, limit: 5, CancellationToken.None);

        Assert.Single(hits);
        Assert.Equal("foo", hits[0].Name);
        Assert.Equal(0.9, hits[0].Score);
        Assert.NotNull(handler.LastUrl);
        Assert.Contains("/api/me/skills/search", handler.LastUrl);
        Assert.Contains("q=q", handler.LastUrl);
        Assert.Contains("limit=5", handler.LastUrl);
    }

    [Fact]
    public async Task SearchAsync_AppendsTagsAsCsv()
    {
        var handler = new RecordingHandler(content: "[]");
        var (client, _) = Create(handler);

        await client.SearchAsync(
            "q",
            scope: null,
            tags: new[] { "a", "b" },
            limit: 10,
            CancellationToken.None);

        Assert.NotNull(handler.LastUrl);
        // "a,b" url-encoded → "a%2Cb"
        Assert.Contains("tags=a%2Cb", handler.LastUrl);
    }

    [Fact]
    public async Task GetByIdAsync_MapsNotFoundToNull()
    {
        var handler = new RecordingHandler(content: "", status: HttpStatusCode.NotFound);
        var (client, _) = Create(handler);

        var result = await client.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByNaturalKeyAsync_EmptyItems_ReturnsNull()
    {
        var handler = new RecordingHandler(
            content: """{"total":0,"skip":0,"take":1,"items":[]}""");
        var (client, _) = Create(handler);

        var result = await client.GetByNaturalKeyAsync("n", "user", "user:u1", CancellationToken.None);

        Assert.Null(result);
        Assert.NotNull(handler.LastUrl);
        Assert.Contains("name=n", handler.LastUrl);
        Assert.Contains("scope=user", handler.LastUrl);
        Assert.Contains("scopeId=user%3Au1", handler.LastUrl);
    }

    [Fact]
    public async Task GetByNaturalKeyAsync_NonEmpty_FollowsUpWithGetById()
    {
        // First response: list envelope with one item.
        // Second response: the bundle for that item.
        var id = Guid.NewGuid();
        var listJson =
            $$"""{"total":1,"skip":0,"take":1,"items":[{"id":"{{id}}","name":"foo","description":null,"scope":"user","scopeId":"user:u1","tags":[],"version":1,"source":null,"updatedAt":"2025-01-01T00:00:00+00:00","createdAt":"2025-01-01T00:00:00+00:00"}]}""";
        var bundleJson =
            $$"""{"skill":{"id":"{{id}}","name":"foo","description":null,"scope":"user","scopeId":"user:u1","tags":[],"version":1,"source":null,"updatedAt":"2025-01-01T00:00:00+00:00","createdAt":"2025-01-01T00:00:00+00:00"},"content":"body","frontmatterJson":"{}","files":[]}""";

        var handler = new RecordingHandler(content: listJson).Enqueue(bundleJson);
        var (client, _) = Create(handler);

        var bundle = await client.GetByNaturalKeyAsync("foo", "user", "user:u1", CancellationToken.None);

        Assert.NotNull(bundle);
        Assert.Equal(id, bundle!.Skill.Id);
        Assert.Equal("body", bundle.Content);
        // Last request should be the follow-up GET /api/me/skills/{id}.
        Assert.NotNull(handler.LastUrl);
        Assert.Contains($"/api/me/skills/{id}", handler.LastUrl);
    }

    [Fact]
    public async Task ListAsync_PassesSkipTakeAndOptionalScopeTags()
    {
        var handler = new RecordingHandler(
            content: """{"total":0,"skip":10,"take":20,"items":[]}""");
        var (client, _) = Create(handler);

        await client.ListAsync(
            scope: "user",
            tags: new[] { "x" },
            skip: 10,
            take: 20,
            CancellationToken.None);

        Assert.NotNull(handler.LastUrl);
        Assert.Contains("skip=10", handler.LastUrl);
        Assert.Contains("take=20", handler.LastUrl);
        Assert.Contains("scope=user", handler.LastUrl);
        Assert.Contains("tags=x", handler.LastUrl);
    }

    [Fact]
    public async Task DeleteAsync_IssuesDeleteToCorrectPath()
    {
        var handler = new RecordingHandler(content: "", status: HttpStatusCode.NoContent);
        var (client, _) = Create(handler);
        var id = Guid.NewGuid();

        await client.DeleteAsync(id, CancellationToken.None);

        Assert.Equal("DELETE", handler.LastMethod);
        Assert.NotNull(handler.LastUrl);
        Assert.Contains($"/api/me/skills/{id}", handler.LastUrl);
    }

    [Fact]
    public async Task ImportAsync_PostsToPluginSyncEndpoint_NoBodyRequired()
    {
        // New endpoint: POST /api/plugin/sync/skills, 202 Accepted, no response body.
        var handler = new RecordingHandler(
            content: "", status: System.Net.HttpStatusCode.Accepted);
        var (client, _) = Create(handler);

        var imported = new ImportedSkill(
            Name: "demo", Description: null, Content: "body",
            FrontmatterJson: "{}", Files: Array.Empty<ImportedSkillFile>(),
            SourcePath: "/virt/demo.md", SuggestedScope: "user",
            SuggestedScopeId: "user:u1", SuggestedTags: Array.Empty<string>());

        await client.ImportAsync(
            "claude-code",
            new[] { imported },
            CancellationToken.None);

        Assert.Equal("POST", handler.LastMethod);
        Assert.NotNull(handler.LastUrl);
        Assert.Contains("/api/plugin/sync/skills", handler.LastUrl);
        Assert.DoesNotContain("/api/me/skills/import", handler.LastUrl);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"adapter\":\"claude-code\"", handler.LastBody);
        Assert.Contains("\"name\":\"demo\"", handler.LastBody);
    }

    [Fact]
    public async Task NetworkFailure_ThrowsCortexUnreachableException()
    {
        var handler = new RecordingHandler { ShouldThrow = true };
        var (client, _) = Create(handler);

        await Assert.ThrowsAsync<CortexUnreachableException>(
            () => client.SearchAsync("q", null, null, 5, CancellationToken.None));
    }
}

