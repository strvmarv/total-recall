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
               content_embedding, embedder_fingerprint, updated_at,
               usage_count, last_used_at, decay_score
        FROM skill_cache
        """;

    public Task UpsertAsync(PluginSyncSkillDto skill, CancellationToken ct)
    {
        using var tx = _conn.BeginTransaction();

        // Look up by natural key to learn if a local row exists (possibly with a
        // different id from a previous local-only scan).
        Guid? existingId = null;
        int existingCount = 0;
        DateTime? existingLastUsed = null;
        using (var q = _conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT id, usage_count, last_used_at
                  FROM skill_cache
                 WHERE name = $n AND scope = $s AND scope_id = $sid
                """;
            q.Parameters.AddWithValue("$n", skill.Name);
            q.Parameters.AddWithValue("$s", skill.Scope);
            q.Parameters.AddWithValue("$sid", skill.ScopeId);
            using var r = q.ExecuteReader();
            if (r.Read())
            {
                existingId = Guid.Parse(r.GetString(0));
                existingCount = r.GetInt32(1);
                existingLastUsed = r.IsDBNull(2)
                    ? null
                    : DateTime.Parse(r.GetString(2)).ToUniversalTime();
            }
        }

        // Identity rewrite: a local row exists with a different id (created from a
        // local scan before any cortex round-trip). Point its usage events at
        // cortex's id, then delete the old row so the upsert below inserts cleanly.
        if (existingId is { } eid && eid != skill.Id)
        {
            using (var rewrite = _conn.CreateCommand())
            {
                rewrite.Transaction = tx;
                rewrite.CommandText = "UPDATE skill_usage_events SET skill_id = $new WHERE skill_id = $old";
                rewrite.Parameters.AddWithValue("$new", skill.Id.ToString());
                rewrite.Parameters.AddWithValue("$old", eid.ToString());
                rewrite.ExecuteNonQuery();
            }
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM skill_cache WHERE id = $old";
                del.Parameters.AddWithValue("$old", eid.ToString());
                del.ExecuteNonQuery();
            }
        }

        // Rename scenario: the same cortex id already exists under a different
        // natural key (e.g. skill was renamed). Delete the old row so the insert
        // below can place it under the new name without a PK collision.
        if (existingId != skill.Id)
        {
            using var chkId = _conn.CreateCommand();
            chkId.Transaction = tx;
            chkId.CommandText = """
                SELECT usage_count, last_used_at FROM skill_cache WHERE id = $id
                """;
            chkId.Parameters.AddWithValue("$id", skill.Id.ToString());
            using var ri = chkId.ExecuteReader();
            if (ri.Read())
            {
                // Carry forward usage from the id-matched row if it's higher.
                var idCount = ri.GetInt32(0);
                if (idCount > existingCount) existingCount = idCount;
                if (!ri.IsDBNull(1))
                {
                    var idLastUsed = DateTime.Parse(ri.GetString(1)).ToUniversalTime();
                    if (existingLastUsed is null || idLastUsed > existingLastUsed)
                        existingLastUsed = idLastUsed;
                }
                ri.Close();
                using var delId = _conn.CreateCommand();
                delId.Transaction = tx;
                delId.CommandText = "DELETE FROM skill_cache WHERE id = $id";
                delId.Parameters.AddWithValue("$id", skill.Id.ToString());
                delId.ExecuteNonQuery();
            }
        }

        // Merge usage_count and last_used_at: max-wins. Cortex's count is the
        // cross-machine aggregate; local count may include events flushed since
        // cortex's snapshot, so picking the max never loses recent local activity.
        var mergedCount = Math.Max(existingCount, skill.UsageCount);
        DateTime? mergedLastUsed = (existingLastUsed, skill.LastUsedAt) switch
        {
            (null, var x)  => x,
            (var x, null)  => x,
            (var a, var b) => a > b ? a : b
        };

        using (var up = _conn.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = """
                INSERT INTO skill_cache (
                    id, name, description, content, frontmatter_json, content_hash,
                    scope, scope_id, tags, source, version, is_orphaned,
                    updated_at, usage_count, last_used_at, decay_score)
                VALUES (
                    $id, $name, $description, $content, $fm, $hash,
                    $scope, $scope_id, $tags, $source, $version, $is_orphaned,
                    $updated_at, $count, $last_used, 0)
                ON CONFLICT(name, scope, scope_id) DO UPDATE SET
                    id               = excluded.id,
                    description      = excluded.description,
                    content          = excluded.content,
                    frontmatter_json = excluded.frontmatter_json,
                    content_hash     = excluded.content_hash,
                    tags             = excluded.tags,
                    source           = excluded.source,
                    version          = excluded.version,
                    is_orphaned      = excluded.is_orphaned,
                    updated_at       = excluded.updated_at,
                    usage_count      = $count,
                    last_used_at     = $last_used;
                """;
            up.Parameters.AddWithValue("$id", skill.Id.ToString());
            up.Parameters.AddWithValue("$name", skill.Name);
            up.Parameters.AddWithValue("$description", (object?)skill.Description ?? DBNull.Value);
            up.Parameters.AddWithValue("$content", skill.Content);
            up.Parameters.AddWithValue("$fm", (object?)skill.FrontmatterJson ?? DBNull.Value);
            up.Parameters.AddWithValue("$hash", (object?)skill.ContentHash ?? DBNull.Value);
            up.Parameters.AddWithValue("$scope", skill.Scope);
            up.Parameters.AddWithValue("$scope_id", skill.ScopeId);
            up.Parameters.AddWithValue("$tags",
                JsonSerializer.Serialize(skill.Tags, SyncJsonContext.Default.StringArray));
            up.Parameters.AddWithValue("$source", (object?)skill.Source ?? DBNull.Value);
            up.Parameters.AddWithValue("$version", skill.Version);
            up.Parameters.AddWithValue("$is_orphaned", skill.IsOrphaned ? 1 : 0);
            up.Parameters.AddWithValue("$updated_at", skill.UpdatedAt.ToString("O"));
            up.Parameters.AddWithValue("$count", mergedCount);
            up.Parameters.AddWithValue("$last_used",
                mergedLastUsed.HasValue ? (object)mergedLastUsed.Value.ToString("O") : DBNull.Value);
            up.ExecuteNonQuery();
        }

        tx.Commit();
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
                FrontmatterJson: null,
                ContentHash: null,
                Scope: reader.GetString(3),
                ScopeId: reader.GetString(4),
                Tags: tags,
                Source: reader.IsDBNull(6) ? null : reader.GetString(6),
                IsOrphaned: reader.GetInt32(8) != 0,
                Version: reader.GetInt32(7),
                UsageCount: 0,
                LastUsedAt: null,
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

    public Task RecordInvocationAsync(Guid skillId, string? host, string? sessionId,
        DateTime occurredAt, CancellationToken ct)
    {
        using var tx = _conn.BeginTransaction();

        using (var ev = _conn.CreateCommand())
        {
            ev.Transaction = tx;
            ev.CommandText = """
                INSERT INTO skill_usage_events (id, skill_id, occurred_at, host, session_id, synced_at)
                VALUES ($id, $skill_id, $occurred_at, $host, $session_id, NULL)
                """;
            ev.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            ev.Parameters.AddWithValue("$skill_id", skillId.ToString());
            ev.Parameters.AddWithValue("$occurred_at", occurredAt.ToString("O"));
            ev.Parameters.AddWithValue("$host", (object?)host ?? DBNull.Value);
            ev.Parameters.AddWithValue("$session_id", (object?)sessionId ?? DBNull.Value);
            ev.ExecuteNonQuery();
        }

        // Read the new count, then write decay_score in the same tx.
        int newCount;
        using (var q = _conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT usage_count FROM skill_cache WHERE id = $id";
            q.Parameters.AddWithValue("$id", skillId.ToString());
            var v = q.ExecuteScalar();
            newCount = (v is long l ? (int)l : 0) + 1;
        }

        // Initial decay at write time (age = 0). Half-life of 30 days = 720 hours;
        // at record time age = 0, so decay_score = newCount * exp(0) = newCount.
        // Search-time ranking (Task 2.4) recomputes with real age.
        var decay = (double)newCount;

        using (var upd = _conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE skill_cache
                   SET usage_count = usage_count + 1,
                       last_used_at = $now,
                       decay_score = $decay
                 WHERE id = $id
                """;
            upd.Parameters.AddWithValue("$id", skillId.ToString());
            upd.Parameters.AddWithValue("$now", occurredAt.ToString("O"));
            upd.Parameters.AddWithValue("$decay", decay);
            upd.ExecuteNonQuery();
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
            if (blob.Length % 4 != 0)
                throw new InvalidOperationException(
                    $"content_embedding blob length {blob.Length} is not a multiple of 4.");
            embedding = new float[blob.Length / 4];
            Buffer.BlockCopy(blob, 0, embedding, 0, blob.Length);
        }
        var usageCount = r.GetInt32(15);
        DateTime? lastUsedAt = r.IsDBNull(16)
            ? null
            : DateTime.Parse(r.GetString(16)).ToUniversalTime();
        var decayScore = r.GetDouble(17);

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
            UpdatedAt: updatedAt,
            UsageCount: usageCount,
            LastUsedAt: lastUsedAt,
            DecayScore: decayScore);
    }
}
