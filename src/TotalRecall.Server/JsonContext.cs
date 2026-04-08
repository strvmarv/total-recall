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
//   - Optional fields are nullable reference types with DefaultIgnoreCondition
//     = WhenWritingNull, so absent fields are simply omitted on the wire.
//
// Task 4.0 does NOT rewire McpServer.cs to use this context — that is Task 4.1.
// This file only declares the types and the source-gen surface.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotalRecall.Server;

// ---------- JSON-RPC envelope ----------

public sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("method")]
    public string Method { get; init; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

public sealed record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; init; }
}

public sealed record JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}

public sealed record NotificationMessage
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; init; } = "";

    [JsonPropertyName("params")]
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
    [JsonPropertyName("listChanged")]
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

    [JsonPropertyName("isError")]
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
// reason / hint / message). `Hint` is omitted on the wire when null because
// JsonContext sets DefaultIgnoreCondition = WhenWritingNull.
public sealed record ModelNotReadyPayload(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("modelName")] string ModelName,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("hint")] string? Hint,
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

// ---------- source-gen context ----------

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
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
public partial class JsonContext : JsonSerializerContext
{
}
