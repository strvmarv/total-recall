using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Skills;

public sealed record SkillBundleDto(
    [property: JsonPropertyName("skill")] SkillDto Skill,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("frontmatterJson")] string FrontmatterJson,
    [property: JsonPropertyName("files")] IReadOnlyList<SkillFileDto> Files);

public sealed record SkillFileDto(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("sizeBytes")] int SizeBytes);
