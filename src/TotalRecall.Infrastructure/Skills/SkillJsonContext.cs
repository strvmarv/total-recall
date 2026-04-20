using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Skills;

/// <summary>
/// AOT-safe source-generated JSON context for skill DTO serialization/deserialization.
/// The DTO records decorate their members with <see cref="JsonPropertyNameAttribute"/>,
/// so no naming policy is required here — the attribute wins regardless.
/// </summary>
[JsonSerializable(typeof(SkillSearchHitDto[]))]
[JsonSerializable(typeof(SkillBundleDto))]
[JsonSerializable(typeof(SkillListResponseDto))]
[JsonSerializable(typeof(SkillImportRequestDto))]
[JsonSerializable(typeof(SkillImportSummaryDto[]))]
internal partial class SkillJsonContext : JsonSerializerContext;
