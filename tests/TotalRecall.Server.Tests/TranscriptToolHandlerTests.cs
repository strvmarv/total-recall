// The three archive tools in their off state (spec 2026-11-18, section 9): each answers from a per-call gate, the input
// schemas are fixed, and the tools are registered only for the sqlite and cortex compositions.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Server.Handlers;
using Xunit;

public sealed class TranscriptToolHandlerTests
{
    private const string OffText =
        "Run `total-recall transcripts enable` in a terminal to turn the archive on.";
    private const string OnText =
        "Searching the archive is not available in this version yet.";

    private static readonly string[] ToolNames = { "transcript_search", "transcript_get", "transcript_sessions" };

    private static IToolHandler Make(string name, Func<bool> isActive) => name switch
    {
        "transcript_search" => new TranscriptSearchHandler(isActive),
        "transcript_get" => new TranscriptGetHandler(isActive),
        "transcript_sessions" => new TranscriptSessionsHandler(isActive),
        _ => throw new ArgumentException(name),
    };

    public static IEnumerable<object[]> Names() => ToolNames.Select(n => new object[] { n });

    private static async Task<JsonElement> Call(IToolHandler h)
    {
        var r = await h.ExecuteAsync(null, CancellationToken.None);
        Assert.NotEqual(true, r.IsError);
        var content = Assert.Single(r.Content);
        Assert.Equal("text", content.Type);
        return JsonDocument.Parse(content.Text).RootElement.Clone();
    }

    [Theory, MemberData(nameof(Names))]
    public async Task Inactive_AnswersEnabledFalseWithTheHint(string name)
    {
        var json = await Call(Make(name, () => false));
        Assert.Equal(2, json.EnumerateObject().Count());
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.Equal(OffText, json.GetProperty("hint").GetString());
    }

    [Theory, MemberData(nameof(Names))]
    public async Task Active_AnswersNotAvailableYet(string name)
    {
        var json = await Call(Make(name, () => true));
        Assert.Equal(3, json.EnumerateObject().Count());
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.False(json.GetProperty("available").GetBoolean());
        Assert.Equal(OnText, json.GetProperty("hint").GetString());
    }

    [Theory, MemberData(nameof(Names))]
    public async Task TheGate_IsReadOnEveryCall(string name)
    {
        var on = false;
        var h = Make(name, () => on);
        Assert.False((await Call(h)).GetProperty("enabled").GetBoolean());
        on = true;
        Assert.True((await Call(h)).GetProperty("enabled").GetBoolean());
        on = false;
        Assert.False((await Call(h)).GetProperty("enabled").GetBoolean());
    }

    [Theory, MemberData(nameof(Names))]
    public void NameAndDescription_StateOffByDefaultAndNotAvailable(string name)
    {
        var h = Make(name, () => false);
        Assert.Equal(name, h.Name);
        Assert.Contains("off by default", h.Description);
        Assert.Contains("not available", h.Description);
    }

    private static JsonElement Prop(IToolHandler h, string name) => h.InputSchema.GetProperty("properties").GetProperty(name);

    private static void AssertType(IToolHandler h, string name, string type) =>
        Assert.Equal(type, Prop(h, name).GetProperty("type").GetString());

    private static string[] Required(IToolHandler h) =>
        h.InputSchema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : Array.Empty<string>();

    private static string[] PropNames(IToolHandler h) =>
        h.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void SearchSchema_MatchesTheSpec()
    {
        var h = new TranscriptSearchHandler(() => false);
        Assert.Equal("object", h.InputSchema.GetProperty("type").GetString());
        Assert.Equal(
            new[] { "query", "scope", "project", "session", "since", "include_automated", "top_k" }.OrderBy(x => x),
            PropNames(h).OrderBy(x => x));
        Assert.Equal(new[] { "query" }, Required(h));
        AssertType(h, "query", "string");
        AssertType(h, "scope", "string");
        Assert.Equal(new[] { "session", "project", "all" }.OrderBy(x => x),
            Prop(h, "scope").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).OrderBy(x => x));
        Assert.Equal("project", Prop(h, "scope").GetProperty("default").GetString());
        AssertType(h, "project", "string");
        AssertType(h, "session", "string");
        AssertType(h, "since", "string");
        AssertType(h, "include_automated", "boolean");
        Assert.False(Prop(h, "include_automated").GetProperty("default").GetBoolean());
        AssertType(h, "top_k", "integer");
        Assert.Equal(1, Prop(h, "top_k").GetProperty("minimum").GetInt32());
        Assert.Equal(50, Prop(h, "top_k").GetProperty("maximum").GetInt32());
        Assert.Equal(10, Prop(h, "top_k").GetProperty("default").GetInt32());
    }

    [Fact]
    public void GetSchema_MatchesTheSpecWithoutOneOf()
    {
        var h = new TranscriptGetHandler(() => false);
        Assert.Equal(
            new[] { "id", "session", "from_exchange", "to_exchange", "context" }.OrderBy(x => x),
            PropNames(h).OrderBy(x => x));
        Assert.Empty(Required(h));
        AssertType(h, "id", "integer");
        AssertType(h, "session", "string");
        AssertType(h, "from_exchange", "integer");
        AssertType(h, "to_exchange", "integer");
        AssertType(h, "context", "integer");
        Assert.Equal(0, Prop(h, "context").GetProperty("minimum").GetInt32());
        Assert.Equal(10, Prop(h, "context").GetProperty("maximum").GetInt32());
        Assert.Equal(1, Prop(h, "context").GetProperty("default").GetInt32());
        var raw = h.InputSchema.GetRawText();
        Assert.DoesNotContain("oneOf", raw);
        Assert.DoesNotContain("anyOf", raw);
        Assert.DoesNotContain("allOf", raw);
    }

    [Fact]
    public void SessionsSchema_MatchesTheSpec()
    {
        var h = new TranscriptSessionsHandler(() => false);
        Assert.Equal(
            new[] { "project", "since", "limit", "include_automated" }.OrderBy(x => x),
            PropNames(h).OrderBy(x => x));
        Assert.Empty(Required(h));
        AssertType(h, "project", "string");
        AssertType(h, "since", "string");
        AssertType(h, "limit", "integer");
        Assert.Equal(1, Prop(h, "limit").GetProperty("minimum").GetInt32());
        Assert.Equal(100, Prop(h, "limit").GetProperty("maximum").GetInt32());
        Assert.Equal(20, Prop(h, "limit").GetProperty("default").GetInt32());
        AssertType(h, "include_automated", "boolean");
        Assert.False(Prop(h, "include_automated").GetProperty("default").GetBoolean());
    }

    // ---- registration points ----

    [Fact]
    public void SqliteRegistry_ContainsTheThreeToolsAnd53Total()
    {
        var home = Path.Combine(Path.GetTempPath(), "tr-tt-" + Guid.NewGuid().ToString("N"));
        var prev = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", home);
            using var handles = ServerComposition.OpenLocalForCatalog(Path.Combine(home, "t.db"));
            foreach (var n in ToolNames) Assert.True(handles.Registry.TryGet(n, out _), n);
            Assert.Equal(53, handles.Registry.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", prev);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CortexRegistry_ContainsTheThreeToolsAnd54Total()
    {
        var home = Path.Combine(Path.GetTempPath(), "tr-tt-" + Guid.NewGuid().ToString("N"));
        var prev = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", home);
            using var handles = ServerComposition.OpenCortexForTest(":memory:", "https://cortex.test", "tr_test123");
            foreach (var n in ToolNames) Assert.True(handles.Registry.TryGet(n, out _), n);
            Assert.Equal(54, handles.Registry.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", prev);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheHandlers_AreConstructedOnlyInOpenSqliteAndOpenCortexCore()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "src", "TotalRecall.Server", "ServerComposition.cs")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!, "src", "TotalRecall.Server", "ServerComposition.cs"));

        // The method each construction sits in: the nearest preceding method header.
        var methodStarts = new[] { "BuildRegistry(", "OpenSqlite(", "OpenPostgres(", "OpenCortexCore(" }
            .Select(m => (Name: m, At: text.IndexOf("ServerCompositionHandles " + m, StringComparison.Ordinal) is var a && a >= 0 ? a : text.IndexOf("ToolRegistry " + m, StringComparison.Ordinal)))
            .Where(x => x.At >= 0).OrderBy(x => x.At).ToArray();
        Assert.Equal(4, methodStarts.Length);

        foreach (var type in new[] { "TranscriptSearchHandler", "TranscriptGetHandler", "TranscriptSessionsHandler" })
        {
            var homes = new List<string>();
            var i = 0;
            while ((i = text.IndexOf("new " + type + "(", i, StringComparison.Ordinal)) >= 0)
            {
                homes.Add(methodStarts.Last(m => m.At < i).Name);
                i++;
            }
            Assert.Equal(new[] { "OpenSqlite(", "OpenCortexCore(" }, homes.OrderBy(h => h == "OpenSqlite(" ? 0 : 1).ToArray());
        }
    }
}
