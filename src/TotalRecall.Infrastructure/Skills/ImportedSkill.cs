using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Skills;

public sealed record ImportedSkill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("frontmatterJson")] string FrontmatterJson,
    [property: JsonPropertyName("files")] IReadOnlyList<ImportedSkillFile> Files,
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("suggestedScope")] string SuggestedScope,
    [property: JsonPropertyName("suggestedScopeId")] string SuggestedScopeId,
    [property: JsonPropertyName("suggestedTags")] IReadOnlyList<string> SuggestedTags);
