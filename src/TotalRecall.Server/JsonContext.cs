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
[JsonSerializable(typeof(JsonElement))]
public partial class JsonContext : JsonSerializerContext
{
}
