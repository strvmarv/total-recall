using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Eval;
using TotalRecall.Infrastructure.Eval;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Eval;

[Collection("ConsoleCapture")]
public sealed class GrowCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter;
    private readonly StringWriter _errWriter;

    public GrowCommandTests()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        _outWriter = new StringWriter();
        _errWriter = new StringWriter();
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    private sealed class FakeGrow : IGrowExecutor
    {
        public List<CandidateRow> Rows { get; } = new();
        public IReadOnlyList<string>? LastAccepts;
        public IReadOnlyList<string>? LastRejects;
        public string? LastPath;

        public IReadOnlyList<CandidateRow> ListPending() => Rows;

        public CandidateResolveResult Resolve(IReadOnlyList<string> accepts, IReadOnlyList<string> rejects, string benchmarkPath)
        {
            LastAccepts = accepts;
            LastRejects = rejects;
            LastPath = benchmarkPath;
            return new CandidateResolveResult(accepts.Count, rejects.Count, new List<string> { "{\"query\":\"x\"}" });
        }
    }

    [Fact]
    public async Task RequiresAction_ReturnsExit2()
    {
        var cmd = new GrowCommand(new FakeGrow());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task UnknownAction_ReturnsExit2()
    {
        var cmd = new GrowCommand(new FakeGrow());
        var code = await cmd.RunAsync(new[] { "nope" });
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task List_RendersTable()
    {
        var fake = new FakeGrow();
        fake.Rows.Add(new CandidateRow(
            Id: "c1",
            QueryText: "some query",
            TopScore: 0.42,
            TopResultContent: "some content",
            TopResultEntryId: "e1",
            FirstSeen: 100,
            LastSeen: 200,
            TimesSeen: 3,
            Status: "pending"));
        var cmd = new GrowCommand(fake);
        var code = await cmd.RunAsync(new[] { "list" });
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Resolve_PassesAcceptAndRejectCsvsToExecutor()
    {
        var fake = new FakeGrow();
        var cmd = new GrowCommand(fake);
        var code = await cmd.RunAsync(new[]
        {
            "resolve",
            "--accept", "a,b,c",
            "--reject", "x",
            "--benchmark", "/tmp/foo.jsonl",
        });
        Assert.Equal(0, code);
        Assert.Equal(new[] { "a", "b", "c" }, fake.LastAccepts);
        Assert.Equal(new[] { "x" }, fake.LastRejects);
        Assert.Equal("/tmp/foo.jsonl", fake.LastPath);
        Assert.Contains("Accepted 3", _outWriter.ToString());
        Assert.Contains("rejected 1", _outWriter.ToString());
    }
}
