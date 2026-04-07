// spike/dotnet/src/TotalRecall.Spike/McpServer.cs
//
// Minimal stdio JSON-RPC server. Implements just enough of the MCP wire protocol
// to handle: initialize, shutdown, tools/list, tools/call.
// Not a full MCP implementation — only what the spike's validation harness exercises.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TotalRecall.Server;

public delegate JsonNode? ToolHandler(JsonNode? arguments);

public sealed class McpServer
{
    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;
    private readonly Dictionary<string, ToolHandler> _tools = new();

    public McpServer(TextReader stdin, TextWriter stdout)
    {
        _stdin = stdin;
        _stdout = stdout;
    }

    public void RegisterTool(string name, ToolHandler handler) => _tools[name] = handler;

    public async Task<int> RunAsync()
    {
        while (true)
        {
            var line = await _stdin.ReadLineAsync();
            if (line is null) return 0;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? req;
            try { req = JsonNode.Parse(line); }
            catch (JsonException ex)
            {
                WriteError(null, -32700, $"Parse error: {ex.Message}");
                continue;
            }

            if (req is null) continue;
            var id = req["id"];
            var method = req["method"]?.GetValue<string>();

            try
            {
                JsonNode? result = method switch
                {
                    "initialize" => HandleInitialize(),
                    "shutdown" => HandleShutdown(),
                    "tools/list" => HandleToolsList(),
                    "tools/call" => HandleToolsCall(req["params"]),
                    _ => null,
                };

                if (method == "shutdown")
                {
                    WriteResult(id, result);
                    return 0;
                }

                if (result is null)
                    WriteError(id, -32601, $"Method not found: {method}");
                else
                    WriteResult(id, result);
            }
            catch (Exception ex)
            {
                WriteError(id, -32603, $"Internal error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static JsonNode HandleInitialize() => new JsonObject
    {
        ["protocolVersion"] = "2024-11-05",
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "total-recall-spike",
            ["version"] = "0.1.0",
        },
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
    };

    private static JsonNode HandleShutdown() => new JsonObject();

    private JsonNode HandleToolsList()
    {
        var arr = new JsonArray();
        foreach (var name in _tools.Keys)
            arr.Add((JsonNode)new JsonObject { ["name"] = name });
        return new JsonObject { ["tools"] = arr };
    }

    private JsonNode? HandleToolsCall(JsonNode? @params)
    {
        var name = @params?["name"]?.GetValue<string>();
        var args = @params?["arguments"];
        if (name is null || !_tools.TryGetValue(name, out var handler))
            throw new InvalidOperationException($"Tool not found: {name}");
        return handler(args);
    }

    private void WriteResult(JsonNode? id, JsonNode? result)
    {
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };
        _stdout.WriteLine(resp.ToJsonString());
        _stdout.Flush();
    }

    private void WriteError(JsonNode? id, int code, string message)
    {
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        _stdout.WriteLine(resp.ToJsonString());
        _stdout.Flush();
    }
}
