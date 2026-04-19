using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Skills;

public sealed record SkillSearchHitDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("scopeId")] string ScopeId,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("excerpt")] string Excerpt);
