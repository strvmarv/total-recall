using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Skills;

public sealed record SkillDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("scopeId")] string ScopeId,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);
