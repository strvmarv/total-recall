# Handlers — Agent Guide

This directory contains **one file per MCP tool handler** (37 total). Each handler is a `sealed class` implementing `IToolHandler`.

---

## IToolHandler Contract

```csharp
public interface IToolHandler
{
    string      Name        { get; }   // Wire tool name; stable; used as dispatch key
    string      Description { get; }   // Surfaced in tools/list
    JsonElement InputSchema { get; }   // JSON Schema for tools/call arguments
    Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct);
}
```

**AOT requirement**: `InputSchema` MUST be built via
`JsonDocument.Parse("""{ ... }""").RootElement.Clone()` in a field initializer or constructor.
Never use `JsonNode` or reflection — this project enforces 0 trim warnings.

---

## Naming Convention

| Tool name | File |
|-----------|------|
| `memory_store` | `MemoryStoreHandler.cs` |
| `kb_ingest_file` | `KbIngestFileHandler.cs` |
| `session_start` | `SessionStartHandler.cs` |
| `eval_report` | `EvalReportHandler.cs` |

Pattern: `PascalCase(tool_name) + Handler.cs`. One class per file, no exceptions.

---

## Constructor Injection (no ToolContext)

There is **no `ToolContext` object**. Dependencies are injected via constructor parameters.
`ServerComposition.BuildRegistry()` constructs every handler with `new` and passes the required singletons directly.

Two lifecycle patterns:

### Shared-Connection (Memory, KB, Session, Status)

Infrastructure objects share the process-lifetime SQLite connection / Postgres data source.
They accept interfaces (`IStore`, `IEmbedder`, `IVectorSearch`, `IHybridSearch`, etc.) and do NOT dispose them.

```csharp
// Typical shared-connection handler
public sealed class MemoryGetHandler(IStore store) : IToolHandler
{
    public string Name => "memory_get";
    // ...
    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        // use store — do NOT dispose it
    }
}
```

### Self-Bootstrapping (Eval, Config, ImportHost, CompactNow, MigrateToRemote)

These handlers open their own short-lived connection per invocation and own its lifecycle.
`new EvalReportHandler()` takes no constructor arguments. The handler calls
`ConfigLoader.GetDbPath()` and `SqliteConnection.Open(...)` internally, disposes after use.

Use self-bootstrapping only when the tool is rarely invoked and long-lived connection sharing
is not needed. Eval and config tools fit this profile.

---

## Error Model

| Situation | Action |
|-----------|--------|
| Missing required argument | `throw new ArgumentException("...")` |
| Invalid argument value | `throw new ArgumentException("...")` |
| Not found (by ID) | Return `ToolCallResult` with `isError: true` and a descriptive message |
| Infrastructure failure | Let it propagate — `McpServer` catches and calls `ErrorTranslator` |

`ErrorTranslator` in `../ErrorTranslator.cs` converts `ArgumentException` → MCP error response.
Never return a success `ToolCallResult` for validation failures.

---

## ToolCallResult Shape

```csharp
// Success with JSON content
return ToolCallResult.Success(JsonSerializer.SerializeToElement(myDto, JsonContext.Default.MyDto));

// Success with text
return ToolCallResult.Text("some message");

// Error
return ToolCallResult.Error("Entry not found: " + id);
```

All DTOs used in responses must be declared in `../JsonContext.cs` (source-generated, AOT-safe).
Never use `JsonSerializer.Serialize(obj)` with the reflection-based overload.

---

## Wiring a New Handler

1. Create `<ToolName>Handler.cs` in this directory — `sealed class`, `IToolHandler`.
2. Add the DTO(s) to `../JsonContext.cs` if the response shape is new.
3. Register in `../ServerComposition.cs` → `BuildRegistry()` in the appropriate group:
   - Memory (12), KB (7), Session (3), Eval (5), Config (2), Misc (4)
4. Update the handler count in the `BuildRegistry` comment.
5. Add a test in `../../../../tests/TotalRecall.Server.Tests/Handlers/`.

**Do NOT** modify `ToolRegistry.cs` — it is a generic map; `BuildRegistry` owns handler registration.

---

## Handler Groups (as registered in BuildRegistry)

| Group | Count | Handlers |
|-------|-------|----------|
| Memory | 12 | store, search, get, update, delete, promote, demote, inspect, history, lineage, export, import |
| KB | 7 | search, ingest_file, ingest_dir, list_collections, refresh, remove, summarize |
| Session | 3 | start, end, context |
| Eval | 5 | report, benchmark, compare, snapshot, grow |
| Config | 2 | get, set |
| Misc | 5 | status, import_host, compact_now, migrate_to_remote, usage_status |

`usage_status` is registered after `BuildRegistry` returns (SQLite-only path in `OpenSqlite`).

---

## Shared Helpers

- `EntryMapping.cs` — maps between storage DTOs and wire-format response shapes
- `ArgumentParsing.cs` — common argument extraction helpers (`GetString`, `GetBool`, etc.)

Use these helpers instead of inline argument parsing to stay consistent.

---

## Testing

Handler tests live in `tests/TotalRecall.Server.Tests/Handlers/`. Use fakes from
`tests/TotalRecall.Cli.Tests/TestSupport/` or in-memory SQLite databases. Handler tests
should validate: argument validation paths, success response shape, and error paths.
