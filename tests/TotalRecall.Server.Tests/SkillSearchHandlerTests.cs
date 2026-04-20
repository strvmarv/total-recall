// Plan 2 Task 9 — SkillSearchHandler contract tests.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class SkillSearchHandlerTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeSkillClient : ISkillClient
    {
        public string? LastQuery;
        public string? LastScope;
        public IReadOnlyList<string>? LastTags;
        public int LastLimit;
        public SkillSearchHitDto[] SearchResult = Array.Empty<SkillSearchHitDto>();

        public Task<SkillSearchHitDto[]> SearchAsync(
            string query, string? scope, IReadOnlyList<string>? tags, int limit, CancellationToken ct)
        {
            LastQuery = query;
            LastScope = scope;
            LastTags = tags;
            LastLimit = limit;
            return Task.FromResult(SearchResult);
        }

        public Task<SkillBundleDto?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillBundleDto?> GetByNaturalKeyAsync(string name, string scope, string scopeId, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillListResponseDto> ListAsync(string? scope, IReadOnlyList<string>? tags, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillImportSummaryDto[]> ImportAsync(string adapter, IReadOnlyList<ImportedSkill> skills, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public void Name_Is_skill_search()
    {
        var handler = new SkillSearchHandler(new FakeSkillClient());
        Assert.Equal("skill_search", handler.Name);
    }

    [Fact]
    public async Task HappyPath_ReturnsSerializedHits()
    {
        var client = new FakeSkillClient
        {
            SearchResult = new[]
            {
                new SkillSearchHitDto(
                    Guid.NewGuid(),
                    "git-committing",
                    "commit helper",
                    "global",
                    "",
                    new[] { "git" },
                    0.92,
                    "Use conventional commits..."),
            },
        };
        var handler = new SkillSearchHandler(client);

        var result = await handler.ExecuteAsync(
            Args("""{"query":"git commit","tags":["git"],"limit":5}"""),
            CancellationToken.None);

        Assert.Equal("git commit", client.LastQuery);
        Assert.Null(client.LastScope);
        Assert.NotNull(client.LastTags);
        Assert.Equal("git", client.LastTags![0]);
        Assert.Equal(5, client.LastLimit);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("git-committing", doc.RootElement[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task DefaultLimit_Is_10()
    {
        var client = new FakeSkillClient();
        var handler = new SkillSearchHandler(client);
        await handler.ExecuteAsync(Args("""{"query":"x"}"""), CancellationToken.None);
        Assert.Equal(10, client.LastLimit);
    }

    [Fact]
    public async Task ScopeArg_IsForwarded()
    {
        var client = new FakeSkillClient();
        var handler = new SkillSearchHandler(client);
        await handler.ExecuteAsync(
            Args("""{"query":"q","scope":"user:paul"}"""),
            CancellationToken.None);
        Assert.Equal("user:paul", client.LastScope);
    }

    [Fact]
    public async Task MissingQuery_Throws()
    {
        var handler = new SkillSearchHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NonObjectArguments_Throws()
    {
        var handler = new SkillSearchHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(null, CancellationToken.None));
    }
}
