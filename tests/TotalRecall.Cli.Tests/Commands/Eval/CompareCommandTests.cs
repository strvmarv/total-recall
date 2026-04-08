using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Eval;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Eval;

[Collection("ConsoleCapture")]
public sealed class CompareCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter;
    private readonly StringWriter _errWriter;

    public CompareCommandTests()
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

    private static RetrievalEventRow Ev(string query, double? topScore, bool? used)
        => new(
            Id: Guid.NewGuid().ToString(),
            Timestamp: 1,
            SessionId: "s",
            QueryText: query,
            QuerySource: "t",
            QueryEmbedding: null,
            ResultsJson: "[]",
            ResultCount: 0,
            TopScore: topScore,
            TopTier: "warm",
            TopContentType: "memory",
            OutcomeUsed: used,
            OutcomeSignal: null,
            ConfigSnapshotId: "cfg",
            LatencyMs: 5,
            TiersSearchedJson: "[]",
            TotalCandidatesScanned: null);

    private static CompareInputs MakeInputs() => new(
        EventsBefore: new List<RetrievalEventRow>
        {
            Ev("q1", 0.9, true),
            Ev("q2", 0.4, false),
        },
        EventsAfter: new List<RetrievalEventRow>
        {
            Ev("q1", 0.9, true),
            Ev("q2", 0.8, true),
        },
        SimilarityThreshold: 0.5,
        BeforeResolvedId: "before-id",
        AfterResolvedId: "after-id",
        RecentSnapshots: Array.Empty<ConfigSnapshotRow>());

    [Fact]
    public async Task RequiresBefore_ReturnsExit2()
    {
        var cmd = new CompareCommand((b, a, d) => MakeInputs());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task HappyPath_Returns0()
    {
        var cmd = new CompareCommand((b, a, d) => MakeInputs());
        var code = await cmd.RunAsync(new[] { "--before", "snapA" });
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task JsonOutput_IsParseable()
    {
        var cmd = new CompareCommand((b, a, d) => MakeInputs());
        var code = await cmd.RunAsync(new[] { "--before", "snapA", "--json" });
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(_outWriter.ToString().Trim());
        var root = doc.RootElement;
        Assert.Equal("before-id", root.GetProperty("beforeId").GetString());
        Assert.Equal("after-id", root.GetProperty("afterId").GetString());
        Assert.True(root.TryGetProperty("deltas", out _));
        Assert.True(root.TryGetProperty("improvements", out _));
    }

    [Fact]
    public async Task UnresolvedBefore_ReturnsExit1()
    {
        var cmd = new CompareCommand((b, a, d) => MakeInputs() with { BeforeResolvedId = null });
        var code = await cmd.RunAsync(new[] { "--before", "missing" });
        Assert.Equal(1, code);
        Assert.Contains("could not resolve --before", _errWriter.ToString());
    }

    [Fact]
    public async Task UnknownArg_ReturnsExit2()
    {
        var cmd = new CompareCommand((b, a, d) => MakeInputs());
        var code = await cmd.RunAsync(new[] { "--before", "x", "--bogus" });
        Assert.Equal(2, code);
    }
}
