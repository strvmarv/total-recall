using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Sync;

namespace TotalRecall.Infrastructure.Skills;

public sealed class SqliteSkillCache : ISkillCache
{
    private readonly SqliteConnection _conn;

    public SqliteSkillCache(SqliteConnection conn) => _conn = conn;

    private const string SelectColumns = """
        SELECT id, name, description, content, frontmatter_json, content_hash,
               scope, scope_id, tags, source, version, is_orphaned,
               content_embedding, embedder_fingerprint, updated_at
        FROM skill_cache
        """;

    public Task UpsertAsync(PluginSyncSkillDto skill, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO skill_cache (id, name, description, scope, scope_id, tags, source, version, is_orphaned, updated_at)
            VALUES ($id, $name, $description, $scope, $scope_id, $tags, $source, $version, $is_orphaned, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                name=excluded.name, description=excluded.description,
                scope=excluded.scope, scope_id=excluded.scope_id,
                tags=excluded.tags, source=excluded.source,
                version=excluded.version, is_orphaned=excluded.is_orphaned,
                updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", skill.Id.ToString());
        cmd.Parameters.AddWithValue("$name", skill.Name);
        cmd.Parameters.AddWithValue("$description", (object?)skill.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scope", skill.Scope);
        cmd.Parameters.AddWithValue("$scope_id", skill.ScopeId);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(skill.Tags, SyncJsonContext.Default.StringArray));
        cmd.Parameters.AddWithValue("$source", (object?)skill.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$version", skill.Version);
        cmd.Parameters.AddWithValue("$is_orphaned", skill.IsOrphaned ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated_at", skill.UpdatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid id, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM skill_cache WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PluginSyncSkillDto>> GetAllAsync(CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, scope, scope_id, tags, source, version, is_orphaned, updated_at FROM skill_cache";
        var results = new List<PluginSyncSkillDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var tagsJson = reader.GetString(5);
            var tags = JsonSerializer.Deserialize(tagsJson, SyncJsonContext.Default.StringArray) ?? Array.Empty<string>();
            var updatedAt = DateTime.Parse(reader.GetString(9)).ToUniversalTime();
            results.Add(new PluginSyncSkillDto(
                Id: Guid.Parse(reader.GetString(0)),
                Name: reader.GetString(1),
                Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                Content: string.Empty,
                Scope: reader.GetString(3),
                ScopeId: reader.GetString(4),
                Tags: tags,
                Source: reader.IsDBNull(6) ? null : reader.GetString(6),
                Version: reader.GetInt32(7),
                IsOrphaned: reader.GetInt32(8) != 0,
                CreatedAt: updatedAt,
                UpdatedAt: updatedAt));
        }
        return Task.FromResult<IReadOnlyList<PluginSyncSkillDto>>(results);
    }

    public Task UpsertScannedAsync(
        ImportedSkill skill, string contentHash,
        byte[]? embedding, string? embedderFingerprint, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO skill_cache (
                id, name, description, content, frontmatter_json, content_hash,
                scope, scope_id, tags, source, version, is_orphaned,
                content_embedding, embedder_fingerprint, updated_at)
            VALUES (
                $id, $name, $description, $content, $fm, $hash,
                $scope, $scope_id, $tags, $source, 1, 0,
                $emb, $emb_fp, $updated_at)
            ON CONFLICT(name, scope, scope_id) DO UPDATE SET
                description       = excluded.description,
                content           = excluded.content,
                frontmatter_json  = excluded.frontmatter_json,
                content_hash      = excluded.content_hash,
                tags              = excluded.tags,
                source            = excluded.source,
                is_orphaned       = 0,
                content_embedding = excluded.content_embedding,
                embedder_fingerprint = excluded.embedder_fingerprint,
                updated_at        = excluded.updated_at,
                version           = skill_cache.version + 1;
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$name", skill.Name);
        cmd.Parameters.AddWithValue("$description", (object?)skill.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$content", skill.Content ?? string.Empty);
        cmd.Parameters.AddWithValue("$fm", (object?)skill.FrontmatterJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", contentHash);
        cmd.Parameters.AddWithValue("$scope", skill.SuggestedScope);
        cmd.Parameters.AddWithValue("$scope_id", skill.SuggestedScopeId);
        cmd.Parameters.AddWithValue("$tags",
            JsonSerializer.Serialize(skill.SuggestedTags ?? Array.Empty<string>(),
                SyncJsonContext.Default.StringArray));
        cmd.Parameters.AddWithValue("$source", (object?)skill.SourcePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$emb", (object?)embedding ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$emb_fp", (object?)embedderFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<CachedSkill?> GetByNaturalKeyAsync(string name, string scope, string scopeId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE name = $name AND scope = $scope AND scope_id = $scope_id LIMIT 1";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$scope_id", scopeId);
        using var r = cmd.ExecuteReader();
        return Task.FromResult(r.Read() ? ReadCachedSkill(r) : null);
    }

    public Task<CachedSkill?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var r = cmd.ExecuteReader();
        return Task.FromResult(r.Read() ? ReadCachedSkill(r) : null);
    }

    public Task<IReadOnlyList<CachedSkill>> ListAllForSearchAsync(CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE is_orphaned = 0";
        var results = new List<CachedSkill>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(ReadCachedSkill(r));
        return Task.FromResult<IReadOnlyList<CachedSkill>>(results);
    }

    public Task MarkOrphansAsync(
        IReadOnlyList<(string Name, string Scope, string ScopeId)> keep, CancellationToken ct)
    {
        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE skill_cache SET is_orphaned = 1";
            cmd.ExecuteNonQuery();
        }
        using (var unmark = _conn.CreateCommand())
        {
            unmark.Transaction = tx;
            unmark.CommandText = "UPDATE skill_cache SET is_orphaned = 0 WHERE name=$n AND scope=$s AND scope_id=$sid";
            var pn = unmark.Parameters.Add("$n", SqliteType.Text);
            var ps = unmark.Parameters.Add("$s", SqliteType.Text);
            var psi = unmark.Parameters.Add("$sid", SqliteType.Text);
            foreach (var (n, s, sid) in keep)
            {
                pn.Value = n; ps.Value = s; psi.Value = sid;
                unmark.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    private static CachedSkill ReadCachedSkill(SqliteDataReader r)
    {
        var tagsJson = r.GetString(8);
        var tags = JsonSerializer.Deserialize(tagsJson, SyncJsonContext.Default.StringArray)
                   ?? Array.Empty<string>();
        var updatedAt = DateTime.Parse(r.GetString(14)).ToUniversalTime();
        float[]? embedding = null;
        if (!r.IsDBNull(12))
        {
            var blob = (byte[])r["content_embedding"];
            embedding = new float[blob.Length / 4];
            Buffer.BlockCopy(blob, 0, embedding, 0, blob.Length);
        }
        return new CachedSkill(
            Id: Guid.Parse(r.GetString(0)),
            Name: r.GetString(1),
            Description: r.IsDBNull(2) ? null : r.GetString(2),
            Content: r.GetString(3),
            FrontmatterJson: r.IsDBNull(4) ? null : r.GetString(4),
            ContentHash: r.IsDBNull(5) ? null : r.GetString(5),
            Scope: r.GetString(6),
            ScopeId: r.GetString(7),
            Tags: tags,
            Source: r.IsDBNull(9) ? null : r.GetString(9),
            Version: r.GetInt32(10),
            IsOrphaned: r.GetInt32(11) != 0,
            ContentEmbedding: embedding,
            EmbedderFingerprint: r.IsDBNull(13) ? null : r.GetString(13),
            UpdatedAt: updatedAt);
    }
}
