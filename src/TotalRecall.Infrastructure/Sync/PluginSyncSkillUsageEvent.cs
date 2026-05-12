using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Sync;

public sealed record PluginSyncSkillUsageEvent(
    [property: JsonPropertyName("skill_id")]    Guid SkillId,
    [property: JsonPropertyName("occurred_at")] DateTime OccurredAt,
    [property: JsonPropertyName("host")]        string? Host,
    [property: JsonPropertyName("session_id")]  string? SessionId);
