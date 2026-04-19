using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Skills;

public sealed record ImportedSkillFile(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("sizeBytes")] int SizeBytes);
