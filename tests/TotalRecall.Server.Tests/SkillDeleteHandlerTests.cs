// Plan 2 Task 12 — SkillDeleteHandler contract tests.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Sync;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class SkillDeleteHandlerTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeSkillClient : ISkillClient
    {
        public Guid? LastId;
        public int DeleteCalls;

        public Task<SkillSearchHitDto[]> SearchAsync(string query, string? scope, IReadOnlyList<string>? tags, int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillBundleDto?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillBundleDto?> GetByNaturalKeyAsync(string name, string scope, string scopeId, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillListResponseDto> ListAsync(string? scope, IReadOnlyList<string>? tags, int skip, int take, CancellationToken ct) => throw new NotImplementedException();

        public Task DeleteAsync(Guid id, CancellationToken ct)
        {
            LastId = id;
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public Task ImportAsync(string adapter, IReadOnlyList<ImportedSkill> skills, CancellationToken ct) => throw new NotImplementedException();
        public Task<PluginSyncSkillDto[]> GetModifiedSinceAsync(DateTime? since, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public void Name_Is_skill_delete()
    {
        var handler = new SkillDeleteHandler(new FakeSkillClient());
        Assert.Equal("skill_delete", handler.Name);
    }

    [Fact]
    public async Task HappyPath_CallsDeleteAndReturnsAck()
    {
        var id = Guid.NewGuid();
        var client = new FakeSkillClient();
        var handler = new SkillDeleteHandler(client);

        var result = await handler.ExecuteAsync(
            Args($$"""{"id":"{{id}}"}"""),
            CancellationToken.None);

        Assert.Equal(1, client.DeleteCalls);
        Assert.Equal(id, client.LastId);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task MissingId_Throws()
    {
        var handler = new SkillDeleteHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NonGuidId_Throws()
    {
        var handler = new SkillDeleteHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"id":"not-a-guid"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NonObjectArguments_Throws()
    {
        var handler = new SkillDeleteHandler(new FakeSkillClient());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(null, CancellationToken.None));
    }
}
