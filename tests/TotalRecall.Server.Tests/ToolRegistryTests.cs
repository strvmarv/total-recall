// tests/TotalRecall.Server.Tests/ToolRegistryTests.cs
//
// Exercises ToolRegistry + IToolHandler (Task 4.2): registration, duplicate
// rejection, tools/list projection (name/description/schema), insertion-order
// preservation, and TryGet lookup semantics. The FakeToolHandler below is a
// configurable stand-in for the real Infrastructure-backed handlers that land
// in Tasks 4.6+; it also records ExecuteAsync calls so routing can be asserted
// end-to-end in a separate dispatch test.

namespace TotalRecall.Server.Tests;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public sealed class ToolRegistryTests
{
    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeToolHandler : IToolHandler
    {
        private readonly Func<JsonElement?, ToolCallResult> _fn;

        public FakeToolHandler(
            string name,
            string description,
            JsonElement inputSchema,
            Func<JsonElement?, ToolCallResult>? fn = null)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema;
            _fn = fn ?? (_ => new ToolCallResult
            {
                Content = new[] { new ToolContent { Text = name } },
            });
        }

        public string Name { get; }
        public string Description { get; }
        public JsonElement InputSchema { get; }

        public int ExecuteCallCount { get; private set; }
        public JsonElement? LastArguments { get; private set; }

        public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
        {
            ExecuteCallCount++;
            LastArguments = arguments;
            return Task.FromResult(_fn(arguments));
        }
    }

    [Fact]
    public void Register_AddsHandler_CountIncreases()
    {
        var registry = new ToolRegistry();
        Assert.Equal(0, registry.Count);

        registry.Register(new FakeToolHandler("a", "A tool", Schema("{\"type\":\"object\"}")));
        Assert.Equal(1, registry.Count);

        registry.Register(new FakeToolHandler("b", "B tool", Schema("{\"type\":\"object\"}")));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Register_Duplicate_Throws()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeToolHandler("dup", "first", Schema("{\"type\":\"object\"}")));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new FakeToolHandler("dup", "second", Schema("{\"type\":\"object\"}"))));
        Assert.Contains("dup", ex.Message);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void ListTools_ReturnsRegisteredSpecs()
    {
        var registry = new ToolRegistry();
        var schema = Schema("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}},\"required\":[\"q\"]}");
        registry.Register(new FakeToolHandler("search", "Searches memory", schema));

        var specs = registry.ListTools();

        Assert.Single(specs);
        var spec = specs[0];
        Assert.Equal("search", spec.Name);
        Assert.Equal("Searches memory", spec.Description);
        Assert.Equal("object", spec.InputSchema.GetProperty("type").GetString());
        Assert.Equal(
            "string",
            spec.InputSchema.GetProperty("properties").GetProperty("q").GetProperty("type").GetString());
    }

    [Fact]
    public void ListTools_PreservesInsertionOrder()
    {
        var registry = new ToolRegistry();
        var schema = Schema("{\"type\":\"object\"}");
        // Register in a non-alphabetical order to make the assertion meaningful.
        registry.Register(new FakeToolHandler("zulu", "", schema));
        registry.Register(new FakeToolHandler("alpha", "", schema));
        registry.Register(new FakeToolHandler("mike", "", schema));

        var specs = registry.ListTools();
        Assert.Equal(3, specs.Count);
        Assert.Equal("zulu", specs[0].Name);
        Assert.Equal("alpha", specs[1].Name);
        Assert.Equal("mike", specs[2].Name);
    }

    [Fact]
    public void TryGet_Known_ReturnsHandler()
    {
        var registry = new ToolRegistry();
        var handler = new FakeToolHandler("ping", "", Schema("{\"type\":\"object\"}"));
        registry.Register(handler);

        Assert.True(registry.TryGet("ping", out var found));
        Assert.Same(handler, found);
    }

    [Fact]
    public void TryGet_Unknown_ReturnsFalse()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeToolHandler("present", "", Schema("{\"type\":\"object\"}")));

        Assert.False(registry.TryGet("absent", out var found));
        Assert.Null(found);
    }

    [Fact]
    public async Task Register_FakeHandler_DispatchRoutesAndRecordsArguments()
    {
        // End-to-end routing proof: register two fakes, dispatch through
        // McpServer, and verify the correct handler saw the correct arguments.
        var registry = new ToolRegistry();
        var a = new FakeToolHandler("a", "", Schema("{\"type\":\"object\"}"));
        var b = new FakeToolHandler("b", "", Schema("{\"type\":\"object\"}"));
        registry.Register(a);
        registry.Register(b);

        var input = new System.IO.StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"b\",\"arguments\":{\"k\":42}}}\n");
        var output = new System.IO.StringWriter();

        await new McpServer(input, output, registry).RunAsync();

        Assert.Equal(0, a.ExecuteCallCount);
        Assert.Equal(1, b.ExecuteCallCount);
        Assert.NotNull(b.LastArguments);
        Assert.Equal(42, b.LastArguments!.Value.GetProperty("k").GetInt32());
    }
}
