using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Skills;

public sealed record SkillImportSummaryDto(
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("scanned")] int Scanned,
    [property: JsonPropertyName("imported")] int Imported,
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("orphaned")] int Orphaned,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors);
