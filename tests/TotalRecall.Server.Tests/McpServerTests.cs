// tests/TotalRecall.Server.Tests/McpServerTests.cs
//
// Drives the McpServer dispatch loop via StringReader/StringWriter, asserting
// the wire-protocol shape and behavior documented in Task 4.1:
//   - initialize returns protocolVersion/serverInfo/capabilities
//   - tools/list reports registered tool metadata (and empty when none)
//   - tools/call routes to the registered handler and echoes its result
//   - unknown methods produce JSON-RPC error -32601
//   - malformed lines produce error -32700 and processing continues
//   - notifications/initialized produces NO response and invokes the callback
//   - shutdown cleanly terminates the loop

namespace TotalRecall.Server.Tests;

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public sealed class McpServerTests
{
    private static JsonElement ParseResponse(string line) =>
        JsonDocument.Parse(line).RootElement.Clone();

    private static string[] Lines(string stdout) =>
        stdout.Replace("\r\n", "\n").TrimEnd('\n').Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

    private static JsonElement EmptyObjectSchema() =>
        JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

    // Minimal in-test fake. Full-featured FakeToolHandler lives alongside
    // ToolRegistryTests; this narrower one keeps McpServerTests focused on
    // dispatch-layer concerns.
    private sealed class InlineHandler : IToolHandler
    {
        private readonly System.Func<JsonElement?, ToolCallResult> _fn;
        public InlineHandler(
            string name,
            string description,
            JsonElement inputSchema,
            System.Func<JsonElement?, ToolCallResult> fn)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema;
            _fn = fn;
        }
        public string Name { get; }
        public string Description { get; }
        public JsonElement InputSchema { get; }
        public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct) =>
            Task.FromResult(_fn(arguments));
    }

    [Fact]
    public async Task Initialize_ReturnsExpectedShape()
    {
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"shutdown\"}\n");
        var output = new System.IO.StringWriter();

        var server = new McpServer(input, output, new ToolRegistry());
        var code = await server.RunAsync();

        Assert.Equal(0, code);
        var lines = Lines(output.ToString());
        Assert.Equal(2, lines.Length);

        var init = ParseResponse(lines[0]);
        Assert.Equal("2.0", init.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, init.GetProperty("id").GetInt32());
        var result = init.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("total-recall", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("0.1.0", result.GetProperty("serverInfo").GetProperty("version").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task ToolsList_EmptyWhenNoneRegistered()
    {
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n");
        var output = new System.IO.StringWriter();

        var server = new McpServer(input, output, new ToolRegistry());
        await server.RunAsync();

        var resp = ParseResponse(Lines(output.ToString())[0]);
        var tools = resp.GetProperty("result").GetProperty("tools");
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);
        Assert.Equal(0, tools.GetArrayLength());
    }

    [Fact]
    public async Task ToolsList_ReturnsRegisteredToolMetadata()
    {
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n");
        var output = new System.IO.StringWriter();

        var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"}}}")
            .RootElement.Clone();
        var registry = new ToolRegistry();
        registry.Register(new InlineHandler(
            name: "echo",
            description: "Echoes input",
            inputSchema: schema,
            fn: _ => new ToolCallResult { Content = new[] { new ToolContent { Text = "ok" } } }));
        var server = new McpServer(input, output, registry);

        await server.RunAsync();

        var resp = ParseResponse(Lines(output.ToString())[0]);
        var tools = resp.GetProperty("result").GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        var tool = tools[0];
        Assert.Equal("echo", tool.GetProperty("name").GetString());
        Assert.Equal("Echoes input", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ToolsCall_RoutesToRegisteredHandlerAndReturnsResult()
    {
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"hello\",\"arguments\":{\"who\":\"world\"}}}\n");
        var output = new System.IO.StringWriter();

        JsonElement? capturedArgs = null;
        var registry = new ToolRegistry();
        registry.Register(new InlineHandler(
            name: "hello",
            description: "",
            inputSchema: EmptyObjectSchema(),
            fn: args =>
            {
                capturedArgs = args;
                return new ToolCallResult
                {
                    Content = new[] { new ToolContent { Text = "hi world" } },
                };
            }));
        var server = new McpServer(input, output, registry);

        await server.RunAsync();

        Assert.NotNull(capturedArgs);
        Assert.Equal("world", capturedArgs!.Value.GetProperty("who").GetString());

        var resp = ParseResponse(Lines(output.ToString())[0]);
        Assert.Equal(7, resp.GetProperty("id").GetInt32());
        var content = resp.GetProperty("result").GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("hi world", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"does/not/exist\"}\n");
        var output = new System.IO.StringWriter();

        await new McpServer(input, output, new ToolRegistry()).RunAsync();

        var resp = ParseResponse(Lines(output.ToString())[0]);
        Assert.Equal(-32601, resp.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(1, resp.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task MalformedLine_ReturnsParseErrorAndContinues()
    {
        var input = new System.IO.StringReader(
            "{not valid json\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\"}\n");
        var output = new System.IO.StringWriter();

        await new McpServer(input, output, new ToolRegistry()).RunAsync();

        var lines = Lines(output.ToString());
        Assert.Equal(2, lines.Length);

        var err = ParseResponse(lines[0]);
        Assert.Equal(-32700, err.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, err.GetProperty("id").ValueKind);

        var ok = ParseResponse(lines[1]);
        Assert.Equal(2, ok.GetProperty("id").GetInt32());
        Assert.True(ok.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task NotificationsInitialized_ProducesNoOutputAndInvokesCallback()
    {
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n");
        var output = new System.IO.StringWriter();

        var callbackCount = 0;
        var tcs = new TaskCompletionSource();
        var server = new McpServer(input, output, new ToolRegistry(), onInitialized: () =>
        {
            callbackCount++;
            tcs.TrySetResult();
            return Task.CompletedTask;
        });

        await server.RunAsync();
        await tcs.Task; // make sure fire-and-forget actually ran

        var lines = Lines(output.ToString());
        // Notification must not emit a response line; only initialize does.
        Assert.Single(lines);
        var resp = ParseResponse(lines[0]);
        Assert.Equal(1, resp.GetProperty("id").GetInt32());
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task NotificationsInitialized_NoCallback_DoesNotCrash()
    {
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n");
        var output = new System.IO.StringWriter();

        var code = await new McpServer(input, output, new ToolRegistry()).RunAsync();
        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Shutdown_TerminatesLoopCleanly()
    {
        // Second line after shutdown must never be processed.
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"shutdown\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\"}\n");
        var output = new System.IO.StringWriter();

        var code = await new McpServer(input, output, new ToolRegistry()).RunAsync();

        Assert.Equal(0, code);
        var lines = Lines(output.ToString());
        Assert.Single(lines);
        var resp = ParseResponse(lines[0]);
        Assert.Equal(1, resp.GetProperty("id").GetInt32());
        Assert.True(resp.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task NotificationsInitialized_CallbackThrows_ErrorWrittenToStderr_LoopContinues()
    {
        // Two messages: a notification that triggers a faulting callback, then
        // a regular initialize that must still get a response (proves the
        // dispatch loop survived the callback fault).
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"initialize\"}\n");
        var output = new System.IO.StringWriter();

        var stderrBuffer = new System.IO.StringWriter();
        var originalStderr = System.Console.Error;
        System.Console.SetError(stderrBuffer);
        try
        {
            var faultObservedTcs = new TaskCompletionSource();
            var server = new McpServer(input, output, new ToolRegistry(), onInitialized: () =>
            {
                // Return a faulted Task so the ContinueWith path executes
                // synchronously and the stderr write is observable before
                // RunAsync returns.
                var t = Task.Run((System.Action)(() => throw new System.InvalidOperationException("boom")));
                _ = t.ContinueWith(_ => faultObservedTcs.TrySetResult());
                return t;
            });

            var code = await server.RunAsync();
            await faultObservedTcs.Task;
            // Yield to give the OnlyOnFaulted continuation in McpServer a chance
            // to run before we read stderr.
            for (var attempt = 0; attempt < 50 && !stderrBuffer.ToString().Contains("faulted"); attempt++)
                await Task.Delay(10);

            Assert.Equal(0, code);
            var lines = Lines(output.ToString());
            Assert.Single(lines);
            var resp = ParseResponse(lines[0]);
            Assert.Equal(7, resp.GetProperty("id").GetInt32());
            Assert.True(resp.TryGetProperty("result", out _));
            Assert.Contains("faulted", stderrBuffer.ToString());
        }
        finally
        {
            System.Console.SetError(originalStderr);
        }
    }

    [Fact]
    public async Task Ping_ReturnsEmptyResult()
    {
        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"ping\"}\n");
        var output = new System.IO.StringWriter();

        await new McpServer(input, output, new ToolRegistry()).RunAsync();

        var resp = ParseResponse(Lines(output.ToString())[0]);
        Assert.Equal(42, resp.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Object, resp.GetProperty("result").ValueKind);
    }
}
