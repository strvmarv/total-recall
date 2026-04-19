using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Skills;

public sealed record SkillImportRequestDto(
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("skills")] IReadOnlyList<ImportedSkill> Skills);
