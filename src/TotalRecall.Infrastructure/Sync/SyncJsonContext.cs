using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// AOT-safe source-generated JSON context for sync telemetry deserialization.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SyncUsageEvent[]))]
[JsonSerializable(typeof(SyncRetrievalEvent[]))]
[JsonSerializable(typeof(SyncCompactionEntry[]))]
internal partial class SyncJsonContext : JsonSerializerContext;
