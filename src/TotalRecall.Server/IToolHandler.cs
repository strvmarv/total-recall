// src/TotalRecall.Server/IToolHandler.cs
//
// Contract for a single MCP tool exposed by TotalRecall.Server.
//
// Task 4.2 introduces this in place of the transitional delegate/registration
// shape carried by Task 4.1. Each handler is an instance that owns its own
// metadata (Name, Description, InputSchema) plus an async ExecuteAsync entry
// point. ToolRegistry (alongside this file) maps name -> IToolHandler and is
// the single source of truth for both tools/list (via InputSchema) and
// tools/call (via ExecuteAsync).
//
// Rationale for the async signature (which deliberately departs from the
// plan's `Task<JsonNode>` phrasing):
//   - JsonElement / JsonElement? keeps the Server project entirely off
//     JsonNode and on source-generated System.Text.Json via JsonContext,
//     preserving AOT trim-safety (0 trim warnings is a hard gate).
//   - ToolCallResult is the typed, source-gen-backed shape McpServer already
//     serializes for the wire; returning it directly avoids a redundant
//     untyped hop.
//   - Infrastructure-backed handlers (memory_store, memory_search, kb_*, etc.)
//     perform real async I/O (SQLite, ONNX embedder warmups, file ingestion),
//     so Task-returning is the right default. Synchronous handlers can simply
//     return Task.FromResult(...).
//   - The CancellationToken parameter is cheap future-proofing for cooperative
//     cancellation; early handlers may ignore it.

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server;

/// <summary>
/// A single MCP tool. Implementations bundle the wire-level metadata used by
/// <c>tools/list</c> together with the <see cref="ExecuteAsync"/> entry point
/// invoked from <c>tools/call</c>.
/// </summary>
public interface IToolHandler
{
    /// <summary>
    /// Wire-level tool name. Must be stable and unique within a given
    /// <see cref="ToolRegistry"/>; used as the dispatch key.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Human-readable description surfaced in <c>tools/list</c>.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema object describing the shape of the <c>arguments</c> payload
    /// this handler expects. Implementers typically build this once via
    /// <c>JsonDocument.Parse("""{...}""").RootElement.Clone()</c> in a field
    /// initializer or constructor so the element is safe to hand out
    /// repeatedly.
    /// </summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// Executes the tool against the supplied <c>arguments</c> JSON. The
    /// <paramref name="arguments"/> parameter is whatever the client passed in
    /// the <c>params.arguments</c> field of <c>tools/call</c>, or
    /// <see langword="null"/> if the field was absent.
    /// </summary>
    Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct);
}
