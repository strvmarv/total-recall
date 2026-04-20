// src/TotalRecall.Server/McpServer.cs
//
// Minimal stdio JSON-RPC server implementing the MCP 2024-11-05 wire protocol
// surface that the TypeScript server (src-ts/tools/registry.ts) exposes, plus
// the baseline SDK-provided methods:
//
//   Requests:        initialize, shutdown, ping, tools/list, tools/call
//   Notifications:   notifications/initialized, notifications/cancelled
//
// Serialization goes through the source-generated JsonContext (Task 4.0) so
// the server stays AOT- and trim-safe. No JsonNode, no untyped reflection
// serializers.
//
// Task 4.2 replaced the transitional ToolRegistration/delegate surface with
// a ToolRegistry + IToolHandler abstraction. McpServer now holds a
// ToolRegistry reference and delegates tools/list and tools/call dispatch to
// it, letting real handlers (Tasks 4.6+) supply their own schemas and async
// ExecuteAsync implementations.

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server;

public sealed class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "total-recall";
    private const string ServerVersion = "0.1.0";

    // Pre-parsed empty JSON object ({}), used as the result payload for
    // methods that return nothing meaningful (ping, shutdown).
    private static readonly JsonElement EmptyObject =
        JsonDocument.Parse("{}").RootElement.Clone();

    // Pre-parsed JSON null, used to force-emit "id":null on error responses
    // whose originating request had no parseable id (per JSON-RPC 2.0 spec).
    // JsonContext's WhenWritingNull strips JsonElement? that are literally null,
    // so we have to hand it an actual JsonElement of kind Null instead.
    private static readonly JsonElement JsonNull =
        JsonDocument.Parse("null").RootElement.Clone();

    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;
    private readonly ToolRegistry _registry;
    private Func<Task>? _onInitialized;

    public McpServer(
        TextReader stdin,
        TextWriter stdout,
        ToolRegistry registry,
        Func<Task>? onInitialized = null)
    {
        _stdin = stdin;
        _stdout = stdout;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _onInitialized = onInitialized;
    }

    /// <summary>Sets (or replaces) the session-init callback fired when the
    /// client sends <c>notifications/initialized</c>.</summary>
    public void SetOnInitialized(Func<Task>? onInitialized) => _onInitialized = onInitialized;

    // TODO(Plan 6+): thread a CancellationToken through this dispatch loop so
    // Ctrl+C / SIGTERM from Host/Program.cs can interrupt a blocking
    // Console.In.ReadLineAsync() instead of waiting for the next message.
    // Discovered during 6.3a review — Program.cs wires CancelKeyPress to a
    // CTS but RunAsync's signature doesn't accept a CT. Disposal still runs
    // via the Host's try/finally, so resources release correctly on signal;
    // this is an in-flight-message responsiveness gap, not a correctness bug.
    public async Task<int> RunAsync()
    {
        while (true)
        {
            var line = await _stdin.ReadLineAsync().ConfigureAwait(false);
            if (line is null) return 0;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonRpcRequest? req;
            try
            {
                req = JsonSerializer.Deserialize(line, JsonContext.Default.JsonRpcRequest);
            }
            catch (JsonException ex)
            {
                WriteError(null, -32700, $"Parse error: {ex.Message}");
                continue;
            }

            if (req is null) continue;

            // JSON-RPC 2.0: a message without an "id" field is a notification
            // and MUST NOT elicit a response. We detect absence via Id.HasValue
            // being false (JsonElement? stays null when the property is missing
            // from the source JSON under source-gen deserialization).
            var isNotification = !req.Id.HasValue;

            if (isNotification)
            {
                HandleNotification(req.Method);
                continue;
            }

            var id = req.Id;
            try
            {
                switch (req.Method)
                {
                    case "initialize":
                        WriteResult(id, HandleInitialize());
                        break;
                    case "ping":
                        WriteResult(id, EmptyObject);
                        break;
                    case "shutdown":
                        WriteResult(id, EmptyObject);
                        return 0;
                    case "tools/list":
                        WriteResult(id, HandleToolsList());
                        break;
                    case "tools/call":
                        // TODO(Task 4.12+): thread a real CancellationToken from
                        // the dispatch loop so notifications/cancelled can unwind
                        // in-flight handlers. For now handlers receive None and
                        // run to completion.
                        var toolResult = await HandleToolsCallAsync(req.Params, CancellationToken.None)
                            .ConfigureAwait(false);
                        WriteResult(id, toolResult);
                        break;
                    default:
                        WriteError(id, -32601, $"Method not found: {req.Method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                // Full exception (type, message, stack, inner) logged to stderr
                // so operators can diagnose the throw site. Wire-level error
                // stays terse so the MCP response isn't bloated for clients.
                Console.Error.WriteLine($"[total-recall] tool dispatch threw on method={req.Method}:");
                Console.Error.WriteLine(ex.ToString());
                WriteError(id, -32603, $"Internal error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void HandleNotification(string method)
    {
        switch (method)
        {
            case "notifications/initialized":
                if (_onInitialized is { } cb)
                {
                    // Fire-and-forget: the SDK contract is that initialized is a
                    // notification, and the TS server kicks session init into the
                    // background without blocking further RPCs. We attach a
                    // ContinueWith on the OnlyOnFaulted path so an unobserved
                    // exception in the callback gets surfaced to stderr instead of
                    // crashing the process via the unhandled-task-exception finalizer
                    // (Task 4.1 review fold-in).
                    var task = cb();
                    if (task is not null)
                    {
                        _ = task.ContinueWith(
                            t => Console.Error.WriteLine(
                                $"total-recall: onInitialized callback faulted: {t.Exception?.GetBaseException()}"),
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    }
                }
                else
                {
                    Console.Error.WriteLine(
                        "total-recall: notifications/initialized received but no onInitialized callback registered");
                }
                break;
            case "notifications/cancelled":
                // We don't support mid-flight cancellation yet; silently consume
                // so the client doesn't see a protocol error. Matches TS behavior,
                // which lets the SDK swallow unknown notifications.
                break;
            default:
                // Unknown notifications are silently ignored per JSON-RPC 2.0.
                break;
        }
    }

    private static InitializeResult HandleInitialize() => new()
    {
        ProtocolVersion = ProtocolVersion,
        ServerInfo = new ServerInfo { Name = ServerName, Version = ServerVersion },
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
    };

    private ToolsListResult HandleToolsList()
    {
        var specs = _registry.ListTools();
        var arr = new ToolSpec[specs.Count];
        for (var i = 0; i < specs.Count; i++) arr[i] = specs[i];
        return new ToolsListResult { Tools = arr };
    }

    private async Task<ToolCallResult> HandleToolsCallAsync(
        JsonElement? paramsElement,
        CancellationToken ct)
    {
        if (!paramsElement.HasValue)
            throw new InvalidOperationException("tools/call: missing params");

        var callParams = JsonSerializer.Deserialize(
            paramsElement.Value.GetRawText(),
            JsonContext.Default.ToolsCallParams);
        if (callParams is null || string.IsNullOrEmpty(callParams.Name))
            throw new InvalidOperationException("tools/call: missing tool name");

        if (!_registry.TryGet(callParams.Name, out var handler) || handler is null)
            throw new InvalidOperationException($"Tool not found: {callParams.Name}");

        return await handler.ExecuteAsync(callParams.Arguments, ct).ConfigureAwait(false);
    }

    // ----- wire output helpers -----

    private void WriteResult<T>(JsonElement? id, T result)
    {
        // Serialize the typed result to a JsonElement via the source-gen
        // context, then stuff it into the response envelope (whose Result
        // field is typed as JsonElement? for shape-agnostic round-tripping).
        var resultJson = result switch
        {
            InitializeResult ir => JsonSerializer.SerializeToElement(ir, JsonContext.Default.InitializeResult),
            ToolsListResult tl => JsonSerializer.SerializeToElement(tl, JsonContext.Default.ToolsListResult),
            ToolCallResult tc => JsonSerializer.SerializeToElement(tc, JsonContext.Default.ToolCallResult),
            JsonElement je => je,
            _ => throw new InvalidOperationException($"Unserializable result type: {typeof(T).FullName}"),
        };

        var resp = new JsonRpcResponse
        {
            Id = id,
            Result = resultJson,
        };
        _stdout.WriteLine(JsonSerializer.Serialize(resp, JsonContext.Default.JsonRpcResponse));
        _stdout.Flush();
    }

    private void WriteError(JsonElement? id, int code, string message)
    {
        var resp = new JsonRpcResponse
        {
            // JSON-RPC 2.0 requires an id on every error response: either the
            // original id if we could parse it, or null if we couldn't.
            Id = id ?? JsonNull,
            Error = new JsonRpcError { Code = code, Message = message },
        };
        _stdout.WriteLine(JsonSerializer.Serialize(resp, JsonContext.Default.JsonRpcResponse));
        _stdout.Flush();
    }
}
