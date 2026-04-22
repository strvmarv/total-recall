using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Sync;

public record PluginSyncSkillDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("tags")] string[] Tags,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("is_orphaned")] bool IsOrphaned,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);
