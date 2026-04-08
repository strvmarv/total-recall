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
public sealed class ReportCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter;
    private readonly StringWriter _errWriter;

    public ReportCommandTests()
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

    private static RetrievalEventRow Event(double? topScore, bool? used, string? tier = "warm", string? ct = "memory", long? lat = 5)
        => new(
            Id: Guid.NewGuid().ToString(),
            Timestamp: 1000,
            SessionId: "s",
            QueryText: "q",
            QuerySource: "test",
            QueryEmbedding: null,
            ResultsJson: "[]",
            ResultCount: 0,
            TopScore: topScore,
            TopTier: tier,
            TopContentType: ct,
            OutcomeUsed: used,
            OutcomeSignal: null,
            ConfigSnapshotId: "cfg",
            LatencyMs: lat,
            TiersSearchedJson: "[]",
            TotalCandidatesScanned: null);

    private static ReportInputs MakeInputs() =>
        new(
            Events: new List<RetrievalEventRow>
            {
                Event(0.9, true),
                Event(0.8, true),
                Event(0.4, false),
            },
            CompactionRows: new List<CompactionAnalyticsRow>
            {
                new("c1", 1, 0.7, 0.1),
            },
            SimilarityThreshold: 0.5);

    [Fact]
    public async Task HappyPath_TableOutput_ReturnsZero()
    {
        var cmd = new ReportCommand(query => MakeInputs());
        var code = await cmd.RunAsync(new[] { "--days", "7" });
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task JsonOutput_IsParseable()
    {
        var cmd = new ReportCommand(query => MakeInputs());
        var code = await cmd.RunAsync(new[] { "--json" });
        Assert.Equal(0, code);
        var stdout = _outWriter.ToString().Trim();
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("totalEvents").GetInt32());
        Assert.True(root.GetProperty("precision").GetDouble() > 0);
        Assert.True(root.TryGetProperty("byTier", out _));
        Assert.True(root.TryGetProperty("compactionHealth", out var ch));
        Assert.Equal(1, ch.GetProperty("totalCompactions").GetInt32());
    }

    [Fact]
    public async Task PassesQueryFiltersToProvider()
    {
        RetrievalEventQuery? captured = null;
        var cmd = new ReportCommand(q => { captured = q; return MakeInputs(); });
        var code = await cmd.RunAsync(new[]
        {
            "--days", "14",
            "--session", "sess-1",
            "--config-snapshot", "cfg-1",
        });
        Assert.Equal(0, code);
        Assert.NotNull(captured);
        Assert.Equal(14, captured!.Days);
        Assert.Equal("sess-1", captured.SessionId);
        Assert.Equal("cfg-1", captured.ConfigSnapshotId);
    }

    [Fact]
    public async Task InvalidDays_ReturnsExit2()
    {
        var cmd = new ReportCommand(q => MakeInputs());
        var code = await cmd.RunAsync(new[] { "--days", "abc" });
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task UnknownArg_ReturnsExit2()
    {
        var cmd = new ReportCommand(q => MakeInputs());
        var code = await cmd.RunAsync(new[] { "--xyz" });
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task ProviderThrows_ReturnsExit1()
    {
        var cmd = new ReportCommand(q => throw new InvalidOperationException("nope"));
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(1, code);
        Assert.Contains("nope", _errWriter.ToString());
    }
}
