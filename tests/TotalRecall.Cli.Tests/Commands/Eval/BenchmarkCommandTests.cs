using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Eval;
using TotalRecall.Infrastructure.Eval;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Eval;

[Collection("ConsoleCapture")]
public sealed class BenchmarkCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter;
    private readonly StringWriter _errWriter;

    public BenchmarkCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-evalcmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private (string corpus, string bench) MakeFiles()
    {
        var corpus = Path.Combine(_tempDir, "c.jsonl");
        File.WriteAllText(corpus, "{\"content\":\"x\",\"type\":\"correction\",\"tags\":[]}\n");
        var bench = Path.Combine(_tempDir, "b.jsonl");
        File.WriteAllText(bench, "{\"query\":\"q\",\"expected_content_contains\":\"x\",\"expected_tier\":\"warm\"}\n");
        return (corpus, bench);
    }

    private static BenchmarkResult FakeResult() =>
        new(TotalQueries: 2,
            ExactMatchRate: 0.5,
            FuzzyMatchRate: 0.75,
            TierRoutingRate: 1.0,
            NegativePassRate: 1.0,
            AvgLatencyMs: 12.34,
            Details: new List<BenchmarkDetail>
            {
                new("q1","x","x found",0.91,true,true,false,true),
                new("q2","y",null,0.0,false,false,false,true),
            });

    [Fact]
    public async Task HappyPath_InvokesExecutorAndPrintsSummary_ReturnsZero()
    {
        var (corpus, bench) = MakeFiles();
        BenchmarkOptions? captured = null;
        var cmd = new BenchmarkCommand((opts, ct) =>
        {
            captured = opts;
            return Task.FromResult(FakeResult());
        });

        var code = await cmd.RunAsync(new[] { "--corpus", corpus, "--benchmark", bench });
        Assert.Equal(0, code);
        Assert.NotNull(captured);
        Assert.Equal(corpus, captured!.CorpusPath);
        Assert.Equal(bench, captured.BenchmarkPath);
    }

    [Fact]
    public async Task MissingCorpusFile_ReturnsExit1()
    {
        var cmd = new BenchmarkCommand((opts, ct) => Task.FromResult(FakeResult()));
        var code = await cmd.RunAsync(new[]
        {
            "--corpus", Path.Combine(_tempDir, "nope.jsonl"),
            "--benchmark", Path.Combine(_tempDir, "alsonope.jsonl"),
        });
        Assert.Equal(1, code);
        Assert.Contains("not found", _errWriter.ToString());
    }

    [Fact]
    public async Task MissingValueAfterFlag_ReturnsExit2()
    {
        var cmd = new BenchmarkCommand((opts, ct) => Task.FromResult(FakeResult()));
        var code = await cmd.RunAsync(new[] { "--corpus" });
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task UnknownArg_ReturnsExit2()
    {
        var cmd = new BenchmarkCommand((opts, ct) => Task.FromResult(FakeResult()));
        var code = await cmd.RunAsync(new[] { "--whatever" });
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task ExecutorThrows_ReturnsExit1()
    {
        var (corpus, bench) = MakeFiles();
        var cmd = new BenchmarkCommand((opts, ct) =>
            Task.FromException<BenchmarkResult>(new InvalidOperationException("boom")));
        var code = await cmd.RunAsync(new[] { "--corpus", corpus, "--benchmark", bench });
        Assert.Equal(1, code);
        Assert.Contains("boom", _errWriter.ToString());
    }
}
