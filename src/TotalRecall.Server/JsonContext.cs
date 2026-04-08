// src/TotalRecall.Server/JsonContext.cs
//
// Source-generated System.Text.Json context for all MCP wire-protocol message
// shapes handled by TotalRecall.Server. Using [JsonSerializable] on a partial
// JsonSerializerContext produces AOT- and trim-safe (de)serializers, avoiding
// the reflection-based code paths that the spike's JsonNode-based McpServer
// relied on.
//
// Notes on shape choices:
//   - JSON-RPC `id` is `string | number | null` on the wire. We model it as
//     JsonElement? so either form round-trips without loss.
//   - Tool arguments and input schemas are arbitrary JSON. JsonElement is
//     AOT-friendly when reached through a source-gen context (unlike JsonNode,
//     which drags reflection-based serializers along).
//   - Task 4.12 flipped the context-wide DefaultIgnoreCondition from
//     WhenWritingNull to Never. Rationale: TS JSON.stringify emits literal
//     `null` for fields whose value is `null` (only `undefined` is omitted),
//     and TR's TS handlers return `null` — not `undefined` — for stubbed
//     optional fields (e.g. StatusHandler's lastCompaction, SessionInit's
//     warmSweep/projectDocs/smokeTest/regressionAlerts). Emitting literal
//     `null` keeps the .NET server byte-compatible with the TS wire shape.
//     Fields that TS deliberately leaves `undefined` (e.g. ModelNotReadyPayload.hint)
//     still need omission — they carry a per-field
//     [JsonIgnore(Condition = WhenWritingNull)] override.
//
// Task 4.0 does NOT rewire McpServer.cs to use this context — that is Task 4.1.
// This file only declares the types and the source-gen surface.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotalRecall.Server;

// ---------- JSON-RPC envelope ----------

// Per-field [JsonIgnore(WhenWritingNull)] on the envelope types preserves
// JSON-RPC 2.0 conformance under the Task 4.12 context-wide flip to
// DefaultIgnoreCondition = Never: a response must carry EITHER "result" OR
// "error", never both. If we let the new Never default through, every
// success response would emit "error":null and every error response would
// emit "result":null — non-compliant. The field-level overrides keep the
// envelope lean while allowing DTO payloads to emit literal nulls.
public sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("method")]
    public string Method { get; init; } = "";

    [JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; init; }
}

public sealed record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; init; }
}

public sealed record JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }
}

public sealed record NotificationMessage
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; init; } = "";

    [JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; init; }
}

// ---------- initialize ----------

public sealed record InitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = "";

    [JsonPropertyName("serverInfo")]
    public ServerInfo ServerInfo { get; init; } = new();

    [JsonPropertyName("capabilities")]
    public ServerCapabilities Capabilities { get; init; } = new();
}

public sealed record ServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";
}

public sealed record ServerCapabilities
{
    [JsonPropertyName("tools")]
    public ToolsCapability? Tools { get; init; }
}

public sealed record ToolsCapability
{
    // TS emits `tools: {}` without listChanged when not supported; retain
    // omission under the Task 4.12 Never default via a per-field override.
    [JsonPropertyName("listChanged"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ListChanged { get; init; }
}

// ---------- tools/list ----------

public sealed record ToolsListResult
{
    [JsonPropertyName("tools")]
    public ToolSpec[] Tools { get; init; } = System.Array.Empty<ToolSpec>();
}

public sealed record ToolSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; init; }
}

// ---------- tools/call ----------

public sealed record ToolsCallParams
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}

public sealed record ToolCallResult
{
    [JsonPropertyName("content")]
    public ToolContent[] Content { get; init; } = System.Array.Empty<ToolContent>();

    // TS only sets isError when true; keep omission semantics under the
    // Task 4.12 Never default.
    [JsonPropertyName("isError"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsError { get; init; }
}

public sealed record ToolContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";
}

// ---------- Task 4.5: ErrorTranslator payload ----------
//
// Wire shape for ModelNotReadyException → MCP tool error response. Mirrors
// the TS `error-translate.ts` payload one-for-one (error / modelName /
// reason / hint / message). TS builds the payload with `hint` assigned
// `undefined` when unset; `JSON.stringify` then OMITS undefined fields. So
// the .NET wire shape must also omit `hint` when null — which we achieve
// with a per-field [JsonIgnore(WhenWritingNull)] override against the
// context-wide DefaultIgnoreCondition = Never default (Task 4.12).
public sealed record ModelNotReadyPayload(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("modelName")] string ModelName,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("hint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint,
    [property: JsonPropertyName("message")] string Message);

// ---------- Task 4.7: memory_search result payload ----------
//
// DTOs serialized by MemorySearchHandler. Field names match the TS
// reference (src-ts/memory/search.ts SearchResult shape) exactly so the
// MCP wire output is byte-compatible: `entry`, `score`, `tier`,
// `content_type`, `rank`. The `entry` object mirrors the fields TS
// exposes on a memory/knowledge row.

public sealed record MemorySearchResultDto(
    [property: JsonPropertyName("entry")] EntryDto Entry,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("rank")] int Rank);

// ---------- Task 4.8: memory_get result payload ----------
//
// Wire shape matches src-ts/memory/get.ts: `{tier, content_type, entry}`.
// Handler returns JSON `null` if no row matches across the 6 tables.
public sealed record MemoryGetResultDto(
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("entry")] EntryDto Entry);

// ---------- Task 4.9: kb_search / kb_ingest_file / kb_ingest_dir payloads ----------
//
// kb_search wire shape mirrors src-ts/tools/kb-tools.ts:190 —
// `{results, hierarchicalMatch, needsSummary}`. KbSearchHandler
// hand-assembles this envelope (rather than using a source-gen DTO) so the
// `hierarchicalMatch: null` sentinel survives JsonContext's
// DefaultIgnoreCondition=WhenWritingNull and stays byte-compatible with
// TS's JSON.stringify output. The `results` array is still source-gen'd
// via MemorySearchResultDto[].

// kb_ingest_file wire shape. Mirrors the .NET IngestFileResult record
// (src/TotalRecall.Infrastructure/Ingestion/FileIngester.cs). Field names
// are snake_case to line up with the TS ingest.ts output as closely as the
// .NET record structure allows.
public sealed record IngestFileResultDto(
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("validation_passed")] bool ValidationPassed,
    [property: JsonPropertyName("validation")] ValidationResultDto Validation);

public sealed record ValidationResultDto(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("probes")] ProbeResultDto[] Probes);

public sealed record ProbeResultDto(
    [property: JsonPropertyName("chunk_index")] int ChunkIndex,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("passed")] bool Passed);

// kb_ingest_dir wire shape. Mirrors IngestDirectoryResult.
public sealed record IngestDirectoryResultDto(
    [property: JsonPropertyName("collection_id")] string CollectionId,
    [property: JsonPropertyName("document_count")] int DocumentCount,
    [property: JsonPropertyName("total_chunks")] int TotalChunks,
    [property: JsonPropertyName("errors")] string[] Errors,
    [property: JsonPropertyName("validation_passed")] bool ValidationPassed,
    [property: JsonPropertyName("validation_failures")] string[] ValidationFailures);

public sealed record EntryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("project")] string? Project,
    [property: JsonPropertyName("tags")] string[] Tags,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("updated_at")] long UpdatedAt,
    [property: JsonPropertyName("last_accessed_at")] long LastAccessedAt,
    [property: JsonPropertyName("access_count")] int AccessCount,
    [property: JsonPropertyName("decay_score")] double DecayScore);

// ---------- Task 4.10: session_end / session_context payloads ----------
//
// session_end currently returns a stub: compactHotTier has not been ported
// to .NET Infrastructure (Plan 5+). The Details field from the TS shape is
// intentionally omitted because source-gen cannot handle `object?` payloads
// and the stub always has nothing to put there.
public sealed record SessionEndResultDto(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("carryForward")] int CarryForward,
    [property: JsonPropertyName("promoted")] int Promoted,
    [property: JsonPropertyName("discarded")] int Discarded);

// session_context wire shape. Mirrors TS session-tools.ts:390-393:
// `{entryCount: int, context: string}`. The context string is the
// formatted hot-tier listing assembled by SessionContextHandler.
public sealed record SessionContextResultDto(
    [property: JsonPropertyName("entryCount")] int EntryCount,
    [property: JsonPropertyName("context")] string Context);

// ---------- Task 4.11: status payload ----------
//
// Structured status shape returned by StatusHandler. Mirrors (a scoped
// subset of) src-ts/tools/system-tools.ts:53-135. Plan 4 scope covers
// tierSizes, knowledgeBase, db, embedding, activity (stub), and
// lastCompaction (stub). Last-session-age lives in the session_start
// domain and is intentionally NOT surfaced here.
public sealed record StatusResultDto(
    [property: JsonPropertyName("tierSizes")] TierSizesDto TierSizes,
    [property: JsonPropertyName("knowledgeBase")] KbStatusDto KnowledgeBase,
    [property: JsonPropertyName("db")] DbStatusDto Db,
    [property: JsonPropertyName("embedding")] EmbeddingStatusDto Embedding,
    [property: JsonPropertyName("activity")] ActivityStatusDto Activity,
    [property: JsonPropertyName("lastCompaction")] LastCompactionDto? LastCompaction);

public sealed record TierSizesDto(
    [property: JsonPropertyName("hot_memories")] int HotMemories,
    [property: JsonPropertyName("hot_knowledge")] int HotKnowledge,
    [property: JsonPropertyName("warm_memories")] int WarmMemories,
    [property: JsonPropertyName("warm_knowledge")] int WarmKnowledge,
    [property: JsonPropertyName("cold_memories")] int ColdMemories,
    [property: JsonPropertyName("cold_knowledge")] int ColdKnowledge);

public sealed record KbStatusDto(
    [property: JsonPropertyName("collections")] KbCollectionSummaryDto[] Collections,
    [property: JsonPropertyName("totalChunks")] int TotalChunks);

public sealed record KbCollectionSummaryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name);

public sealed record DbStatusDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
    [property: JsonPropertyName("sessionId")] string SessionId);

public sealed record EmbeddingStatusDto(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("dimensions")] int Dimensions);

public sealed record ActivityStatusDto(
    [property: JsonPropertyName("retrievals7d")] int Retrievals7d,
    [property: JsonPropertyName("avgTopScore7d")] double? AvgTopScore7d,
    [property: JsonPropertyName("positiveOutcomes7d")] int PositiveOutcomes7d,
    [property: JsonPropertyName("negativeOutcomes7d")] int NegativeOutcomes7d);

public sealed record LastCompactionDto(
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("reason")] string Reason);

// ---- Task 6.0a: memory admin DTOs ----
//
// Wire shapes for the 7 new MCP handlers added in Plan 6 Task 6.0a
// (memory_promote, memory_demote, memory_inspect, memory_history,
// memory_lineage, memory_export, memory_import). These mirror the CLI
// command outputs but are serialized through source-gen JsonContext
// instead of the CLI's hand-rolled JSON writers, so AOT publish stays
// trim-clean.

public sealed record MemoryMoveResultDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("from_tier")] string FromTier,
    [property: JsonPropertyName("from_content_type")] string FromContentType,
    [property: JsonPropertyName("to_tier")] string ToTier,
    [property: JsonPropertyName("to_content_type")] string ToContentType,
    [property: JsonPropertyName("success")] bool Success);

// Full-fat entry detail for memory_inspect. Extends EntryDto with the
// location (tier / content_type), source_tool / parent_id / collection_id /
// metadata that CLI `memory inspect` also shows, and an optional
// compaction_history entry sourced from CompactionLog.GetByTargetEntryId.
public sealed record MemoryInspectResultDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("source_tool")] string? SourceTool,
    [property: JsonPropertyName("project")] string? Project,
    [property: JsonPropertyName("tags")] string[] Tags,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("updated_at")] long UpdatedAt,
    [property: JsonPropertyName("last_accessed_at")] long LastAccessedAt,
    [property: JsonPropertyName("access_count")] int AccessCount,
    [property: JsonPropertyName("decay_score")] double DecayScore,
    [property: JsonPropertyName("parent_id")] string? ParentId,
    [property: JsonPropertyName("collection_id")] string? CollectionId,
    [property: JsonPropertyName("metadata")] string Metadata,
    [property: JsonPropertyName("compaction_history")] CompactionMovementDto? CompactionHistory);

// Wire projection of an Infrastructure.Telemetry.CompactionMovementRow.
// Used by both memory_history (array) and memory_inspect (single, optional).
public sealed record CompactionMovementDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("source_tier")] string SourceTier,
    [property: JsonPropertyName("target_tier")] string? TargetTier,
    [property: JsonPropertyName("source_entry_ids")] string[] SourceEntryIds,
    [property: JsonPropertyName("target_entry_id")] string? TargetEntryId,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record MemoryHistoryResultDto(
    [property: JsonPropertyName("movements")] CompactionMovementDto[] Movements,
    [property: JsonPropertyName("count")] int Count);

// Lineage tree node. Sources is null for leaf nodes so the field stays
// consistent with the CLI hand-rolled shape (which omits empty sources on
// leaves via conditional key emission).
public sealed record LineageNodeDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("compaction_log_id")] string? CompactionLogId,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("timestamp")] long? Timestamp,
    [property: JsonPropertyName("source_tier")] string? SourceTier,
    [property: JsonPropertyName("target_tier")] string? TargetTier,
    [property: JsonPropertyName("sources")] LineageNodeDto[]? Sources);

// memory_export entry — mirrors the export envelope row. Kept separate
// from EntryDto because the export shape carries (tier, content_type,
// source_tool, parent_id, collection_id, metadata) alongside the core
// Entry fields, and re-uses the same snake_case wire names the CLI's
// hand-rolled writer emits.
public sealed record ExportEntryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("source_tool")] string? SourceTool,
    [property: JsonPropertyName("project")] string? Project,
    [property: JsonPropertyName("tags")] string[] Tags,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("updated_at")] long UpdatedAt,
    [property: JsonPropertyName("last_accessed_at")] long LastAccessedAt,
    [property: JsonPropertyName("access_count")] int AccessCount,
    [property: JsonPropertyName("decay_score")] double DecayScore,
    [property: JsonPropertyName("parent_id")] string? ParentId,
    [property: JsonPropertyName("collection_id")] string? CollectionId,
    [property: JsonPropertyName("metadata")] string Metadata,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("content_type")] string ContentType);

public sealed record MemoryExportResultDto(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("exported_at")] long ExportedAt,
    [property: JsonPropertyName("entries")] ExportEntryDto[] Entries);

public sealed record MemoryImportResultDto(
    [property: JsonPropertyName("imported_count")] int ImportedCount,
    [property: JsonPropertyName("skipped_count")] int SkippedCount,
    [property: JsonPropertyName("errors")] string[] Errors);

// ---------- source-gen context ----------

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(NotificationMessage))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(ServerCapabilities))]
[JsonSerializable(typeof(ToolsCapability))]
[JsonSerializable(typeof(ToolsListResult))]
[JsonSerializable(typeof(ToolSpec))]
[JsonSerializable(typeof(ToolsCallParams))]
[JsonSerializable(typeof(ToolCallResult))]
[JsonSerializable(typeof(ToolContent))]
[JsonSerializable(typeof(ModelNotReadyPayload))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(MemorySearchResultDto))]
[JsonSerializable(typeof(MemorySearchResultDto[]))]
[JsonSerializable(typeof(EntryDto))]
[JsonSerializable(typeof(MemoryGetResultDto))]
[JsonSerializable(typeof(IngestFileResultDto))]
[JsonSerializable(typeof(ValidationResultDto))]
[JsonSerializable(typeof(ProbeResultDto))]
[JsonSerializable(typeof(ProbeResultDto[]))]
[JsonSerializable(typeof(IngestDirectoryResultDto))]
// ---- Task 4.3: SessionLifecycle wire shapes ----
// Source-gen needs each nested record AND each generic collection element
// type registered explicitly so the AOT publisher does not have to walk
// reflection metadata. The IReadOnlyList<T> properties on SessionInitResult
// are covered by registering List<T> for each element type below.
[JsonSerializable(typeof(SessionInitResult))]
[JsonSerializable(typeof(ImportSummaryRow))]
[JsonSerializable(typeof(TierSummary))]
[JsonSerializable(typeof(WarmSweepResult))]
[JsonSerializable(typeof(ProjectDocsResult))]
[JsonSerializable(typeof(SmokeTestResult))]
[JsonSerializable(typeof(RegressionAlert))]
[JsonSerializable(typeof(System.Collections.Generic.List<ImportSummaryRow>))]
[JsonSerializable(typeof(System.Collections.Generic.List<RegressionAlert>))]
[JsonSerializable(typeof(System.Collections.Generic.List<string>))]
// ---- Task 4.10: session_end / session_context DTOs ----
[JsonSerializable(typeof(SessionEndResultDto))]
[JsonSerializable(typeof(SessionContextResultDto))]
// ---- Task 4.11: status DTOs ----
[JsonSerializable(typeof(StatusResultDto))]
[JsonSerializable(typeof(TierSizesDto))]
[JsonSerializable(typeof(KbStatusDto))]
[JsonSerializable(typeof(KbCollectionSummaryDto))]
[JsonSerializable(typeof(KbCollectionSummaryDto[]))]
[JsonSerializable(typeof(DbStatusDto))]
[JsonSerializable(typeof(EmbeddingStatusDto))]
[JsonSerializable(typeof(ActivityStatusDto))]
[JsonSerializable(typeof(LastCompactionDto))]
// ---- Task 6.0a: memory admin DTOs ----
[JsonSerializable(typeof(MemoryMoveResultDto))]
[JsonSerializable(typeof(MemoryInspectResultDto))]
[JsonSerializable(typeof(CompactionMovementDto))]
[JsonSerializable(typeof(CompactionMovementDto[]))]
[JsonSerializable(typeof(MemoryHistoryResultDto))]
[JsonSerializable(typeof(LineageNodeDto))]
[JsonSerializable(typeof(LineageNodeDto[]))]
[JsonSerializable(typeof(ExportEntryDto))]
[JsonSerializable(typeof(ExportEntryDto[]))]
[JsonSerializable(typeof(MemoryExportResultDto))]
[JsonSerializable(typeof(MemoryImportResultDto))]
public partial class JsonContext : JsonSerializerContext
{
}
