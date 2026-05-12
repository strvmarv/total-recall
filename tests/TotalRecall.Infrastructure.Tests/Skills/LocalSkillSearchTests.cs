using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Skills;

public class LocalSkillSearchTests
{
    [Fact]
    public async Task SearchAsync_PrefersSemanticHitOverKeywordOnly()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var cache = new SqliteSkillCache(conn);

        await SeedSkill(cache, "deploy-checklist",
            "Steps to deploy a service safely", new float[] { 1f, 0f, 0f });
        await SeedSkill(cache, "ship-it",
            "deploy mentioned but unrelated to safety", new float[] { 0f, 1f, 0f });

        var embedder = new FixedEmbedder(new float[] { 0.99f, 0.1f, 0f });
        var search = new LocalSkillSearch(cache, embedder);

        var hits = await search.SearchAsync("how to deploy safely", tags: null, limit: 2,
            CancellationToken.None);
        Assert.Equal(2, hits.Count);
        Assert.Equal("deploy-checklist", hits[0].Name);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenCacheEmpty()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var cache = new SqliteSkillCache(conn);
        var embedder = new FixedEmbedder(new float[] { 1f, 0f });
        var search = new LocalSkillSearch(cache, embedder);

        var hits = await search.SearchAsync("anything", tags: null, limit: 10, CancellationToken.None);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchAsync_RespectsLimit()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var cache = new SqliteSkillCache(conn);

        await SeedSkill(cache, "skill-a", "content a", new float[] { 1f, 0f });
        await SeedSkill(cache, "skill-b", "content b", new float[] { 0f, 1f });
        await SeedSkill(cache, "skill-c", "content c", new float[] { 0.5f, 0.5f });

        var embedder = new FixedEmbedder(new float[] { 1f, 0f });
        var search = new LocalSkillSearch(cache, embedder);

        var hits = await search.SearchAsync("content", tags: null, limit: 2, CancellationToken.None);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task SearchAsync_FiltersByTags()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var cache = new SqliteSkillCache(conn);

        await SeedSkillWithTags(cache, "tagged-skill", "relevant content", new float[] { 1f, 0f }, new[] { "ops", "deploy" });
        await SeedSkillWithTags(cache, "other-skill", "relevant content", new float[] { 1f, 0f }, new[] { "dev" });

        var embedder = new FixedEmbedder(new float[] { 1f, 0f });
        var search = new LocalSkillSearch(cache, embedder);

        var hits = await search.SearchAsync("relevant", tags: new[] { "ops" }, limit: 10, CancellationToken.None);
        Assert.Single(hits);
        Assert.Equal("tagged-skill", hits[0].Name);
    }

    private static async Task SeedSkill(SqliteSkillCache cache, string name, string content, float[] emb)
        => await SeedSkillWithTags(cache, name, content, emb, Array.Empty<string>());

    private static async Task SeedSkillWithTags(SqliteSkillCache cache, string name, string content, float[] emb, string[] tags)
    {
        var bytes = new byte[emb.Length * 4];
        Buffer.BlockCopy(emb, 0, bytes, 0, bytes.Length);
        var imported = new ImportedSkill(
            Name: name,
            Description: null,
            Content: content,
            FrontmatterJson: "{}",
            Files: Array.Empty<ImportedSkillFile>(),
            SourcePath: $"fixtures/{name}.md",
            SuggestedScope: "user",
            SuggestedScopeId: "u1",
            SuggestedTags: tags);
        await cache.UpsertScannedAsync(imported,
            SkillContentHash.Compute(content), bytes, "test-fp", CancellationToken.None);
    }

    private sealed class FixedEmbedder : IEmbedder
    {
        private readonly float[] _vec;
        public FixedEmbedder(float[] v) { _vec = v; }
        public float[] Embed(string text) => _vec;
        public EmbedderDescriptor Descriptor =>
            new EmbedderDescriptor("test", "fixed", "1", _vec.Length);
    }
}
