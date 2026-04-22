using System.Text.Json;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Sync;

namespace TotalRecall.Infrastructure.Skills;

public sealed class SqliteSkillCache : ISkillCache
{
    private readonly SqliteConnection _conn;

    public SqliteSkillCache(SqliteConnection conn) => _conn = conn;

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
}
