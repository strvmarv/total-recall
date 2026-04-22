// Plan 2 Task 11 — SkillListHandler contract tests.

using System.Text;
using System.Text.Json;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Sync;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class SkillListHandlerTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeSkillClient : ISkillClient
    {
        public string? LastScope;
        public IReadOnlyList<string>? LastTags;
        public int LastSkip;
        public int LastTake;
        public SkillListResponseDto Result = new(0, 0, 0, Array.Empty<SkillDto>());

        public Task<SkillSearchHitDto[]> SearchAsync(string query, string? scope, IReadOnlyList<string>? tags, int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillBundleDto?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillBundleDto?> GetByNaturalKeyAsync(string name, string scope, string scopeId, CancellationToken ct) => throw new NotImplementedException();

        public Task<SkillListResponseDto> ListAsync(string? scope, IReadOnlyList<string>? tags, int skip, int take, CancellationToken ct)
        {
            LastScope = scope;
            LastTags = tags;
            LastSkip = skip;
            LastTake = take;
            return Task.FromResult(Result);
        }

        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task ImportAsync(string adapter, IReadOnlyList<ImportedSkill> skills, CancellationToken ct) => throw new NotImplementedException();
        public Task<PluginSyncSkillDto[]> GetModifiedSinceAsync(DateTime? since, CancellationToken ct) => throw new NotImplementedException();
    }

    private static SkillDto SampleDto(string name) => new(
        Guid.NewGuid(),
        name,
        "desc",
        "global",
        "",
        Array.Empty<string>(),
        1,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    [Fact]
    public void Name_Is_skill_list()
    {
        var handler = new SkillListHandler(new FakeSkillClient());
        Assert.Equal("skill_list", handler.Name);
    }

    [Fact]
    public async Task DefaultPaging_EmitsNextCursor_WhenMorePages()
    {
        // Total = 100, skip = 0, take = 50, items = 50 → next cursor = base64("50")
        var items = Enumerable.Range(0, 50).Select(i => SampleDto($"s{i}")).ToArray();
        var client = new FakeSkillClient { Result = new SkillListResponseDto(100, 0, 50, items) };
        var handler = new SkillListHandler(client);

        var result = await handler.ExecuteAsync(Args("""{}"""), CancellationToken.None);

        Assert.Equal(0, client.LastSkip);
        Assert.Equal(50, client.LastTake);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(50, doc.RootElement.GetProperty("items").GetArrayLength());
        var cursor = doc.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.Equal("50", Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));
    }

    [Fact]
    public async Task WithCursor_DecodesSkip()
    {
        var items = new[] { SampleDto("a") };
        var client = new FakeSkillClient { Result = new SkillListResponseDto(101, 50, 50, items) };
        var handler = new SkillListHandler(client);

        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("50"));
        await handler.ExecuteAsync(
            Args($$"""{"cursor":"{{cursor}}"}"""),
            CancellationToken.None);

        Assert.Equal(50, client.LastSkip);
    }

    [Fact]
    public async Task LastPage_OmitsNextCursor()
    {
        // Total = 10, skip = 0, items = 10 → no next page.
        var items = Enumerable.Range(0, 10).Select(i => SampleDto($"s{i}")).ToArray();
        var client = new FakeSkillClient { Result = new SkillListResponseDto(10, 0, 50, items) };
        var handler = new SkillListHandler(client);

        var result = await handler.ExecuteAsync(Args("""{}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.False(doc.RootElement.TryGetProperty("nextCursor", out _));
        Assert.Equal(10, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task EmptyItems_Serializes()
    {
        var client = new FakeSkillClient { Result = new SkillListResponseDto(0, 0, 50, Array.Empty<SkillDto>()) };
        var handler = new SkillListHandler(client);

        var result = await handler.ExecuteAsync(Args("""{}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("nextCursor", out _));
    }

    [Fact]
    public async Task BadCursor_Throws()
    {
        var handler = new SkillListHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"cursor":"not-base64!!"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NegativeSkipCursor_Throws()
    {
        var negCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("-5"));
        var handler = new SkillListHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(
                Args($$"""{"cursor":"{{negCursor}}"}"""),
                CancellationToken.None));
    }

    [Fact]
    public async Task ScopeAndTags_AreForwarded()
    {
        var client = new FakeSkillClient();
        var handler = new SkillListHandler(client);
        await handler.ExecuteAsync(
            Args("""{"scope":"user:paul","tags":["git"],"limit":25}"""),
            CancellationToken.None);

        Assert.Equal("user:paul", client.LastScope);
        Assert.NotNull(client.LastTags);
        Assert.Equal("git", client.LastTags![0]);
        Assert.Equal(25, client.LastTake);
    }
}
