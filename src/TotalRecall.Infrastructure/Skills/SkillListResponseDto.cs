using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Skills;

public sealed record SkillListResponseDto(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("skip")] int Skip,
    [property: JsonPropertyName("take")] int Take,
    [property: JsonPropertyName("items")] IReadOnlyList<SkillDto> Items);
