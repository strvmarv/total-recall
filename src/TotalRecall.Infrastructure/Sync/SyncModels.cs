using System.Text.Json.Serialization;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>A single memory entry in the sync protocol (push/pull).</summary>
public sealed record SyncEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("entry_type")] string EntryType,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("tags")] string[] Tags,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("access_count")] int AccessCount,
    [property: JsonPropertyName("decay_score")] double DecayScore,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("deleted_at")] DateTime? DeletedAt = null,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("tier")] string? Tier = null);

/// <summary>Result of pulling memories modified since a given watermark.</summary>
public sealed record SyncPullResult(
    [property: JsonPropertyName("memories")] SyncEntry[] Memories,
    [property: JsonPropertyName("tombstone_horizon")] DateTime? TombstoneHorizon);

/// <summary>A single search hit returned by the remote backend.</summary>
public sealed record SyncSearchResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("tags")] string[]? Tags);

/// <summary>Tier-bucketed memory counts and total size.</summary>
public sealed record SyncStatusResult(
    [property: JsonPropertyName("hot")] int Hot,
    [property: JsonPropertyName("warm")] int Warm,
    [property: JsonPropertyName("cold")] int Cold,
    [property: JsonPropertyName("kb")] int Kb);

/// <summary>A usage telemetry event to push to the remote backend.</summary>
public sealed record SyncUsageEvent(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("project")] string? Project,
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens,
    [property: JsonPropertyName("cache_creation_tokens")] int? CacheCreationTokens,
    [property: JsonPropertyName("cache_read_tokens")] int? CacheReadTokens,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp);

/// <summary>A retrieval telemetry event to push to the remote backend.</summary>
public sealed record SyncRetrievalEvent(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("tiers_searched")] string[] TiersSearched,
    [property: JsonPropertyName("top_k")] int TopK,
    [property: JsonPropertyName("top_score")] double TopScore,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("latency_ms")] double LatencyMs,
    [property: JsonPropertyName("outcome_signal")] string? OutcomeSignal,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp);

/// <summary>A compaction log entry to push to the remote backend.</summary>
public sealed record SyncCompactionEntry(
    [property: JsonPropertyName("entry_id")] string EntryId,
    [property: JsonPropertyName("from_tier")] string FromTier,
    [property: JsonPropertyName("to_tier")] string ToTier,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("semantic_drift")] double? SemanticDrift,
    [property: JsonPropertyName("decay_score")] double? DecayScore,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp);
