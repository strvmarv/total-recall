// Plan 2 Task 10 — SkillGetHandler contract tests.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class SkillGetHandlerTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeSkillClient : ISkillClient
    {
        public Guid? LastId;
        public (string Name, string Scope, string ScopeId)? LastNaturalKey;
        public SkillBundleDto? BundleResult;

        public Task<SkillSearchHitDto[]> SearchAsync(string query, string? scope, IReadOnlyList<string>? tags, int limit, CancellationToken ct) => throw new NotImplementedException();

        public Task<SkillBundleDto?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            LastId = id;
            return Task.FromResult(BundleResult);
        }

        public Task<SkillBundleDto?> GetByNaturalKeyAsync(string name, string scope, string scopeId, CancellationToken ct)
        {
            LastNaturalKey = (name, scope, scopeId);
            return Task.FromResult(BundleResult);
        }

        public Task<SkillListResponseDto> ListAsync(string? scope, IReadOnlyList<string>? tags, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillImportSummaryDto[]> ImportAsync(string adapter, IReadOnlyList<ImportedSkill> skills, CancellationToken ct) => throw new NotImplementedException();
    }

    private static SkillBundleDto SampleBundle(Guid id) => new(
        new SkillDto(
            id,
            "git-committing",
            "desc",
            "global",
            "",
            new[] { "git" },
            1,
            "claude-code",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow),
        "content",
        "{}",
        Array.Empty<SkillFileDto>());

    [Fact]
    public void Name_Is_skill_get()
    {
        var handler = new SkillGetHandler(new FakeSkillClient());
        Assert.Equal("skill_get", handler.Name);
    }

    [Fact]
    public async Task ById_CallsGetByIdAsync()
    {
        var id = Guid.NewGuid();
        var client = new FakeSkillClient { BundleResult = SampleBundle(id) };
        var handler = new SkillGetHandler(client);

        var result = await handler.ExecuteAsync(
            Args($$"""{"id":"{{id}}"}"""),
            CancellationToken.None);

        Assert.Equal(id, client.LastId);
        Assert.Null(client.LastNaturalKey);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(id.ToString(), doc.RootElement.GetProperty("skill").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ByNaturalKey_CallsGetByNaturalKeyAsync()
    {
        var id = Guid.NewGuid();
        var client = new FakeSkillClient { BundleResult = SampleBundle(id) };
        var handler = new SkillGetHandler(client);

        var result = await handler.ExecuteAsync(
            Args("""{"name":"git-committing","scope":"global","scopeId":""}"""),
            CancellationToken.None);

        Assert.Null(client.LastId);
        Assert.NotNull(client.LastNaturalKey);
        Assert.Equal("git-committing", client.LastNaturalKey!.Value.Name);
        Assert.Equal("global", client.LastNaturalKey.Value.Scope);
        Assert.Equal("", client.LastNaturalKey.Value.ScopeId);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("git-committing", doc.RootElement.GetProperty("skill").GetProperty("name").GetString());
    }

    [Fact]
    public async Task BothShapes_Throws()
    {
        var handler = new SkillGetHandler(new FakeSkillClient());
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(
                Args($$"""{"id":"{{id}}","name":"git","scope":"global","scopeId":""}"""),
                CancellationToken.None));
    }

    [Fact]
    public async Task NeitherShape_Throws()
    {
        var handler = new SkillGetHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task PartialNaturalKey_Throws()
    {
        var handler = new SkillGetHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(
                Args("""{"name":"git","scope":"global"}"""),
                CancellationToken.None));
    }

    [Fact]
    public async Task NonGuidId_Throws()
    {
        var handler = new SkillGetHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"id":"not-a-guid"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NotFound_ReturnsJsonNull()
    {
        var client = new FakeSkillClient { BundleResult = null };
        var handler = new SkillGetHandler(client);
        var id = Guid.NewGuid();

        var result = await handler.ExecuteAsync(
            Args($$"""{"id":"{{id}}"}"""),
            CancellationToken.None);

        Assert.Equal("null", result.Content[0].Text);
    }
}
