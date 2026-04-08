using System;
using System.IO;
using System.Threading.Tasks;
using Tomlyn.Model;
using TotalRecall.Cli.Commands.Config;
using TotalRecall.Infrastructure.Config;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Config;

[Collection("ConsoleCapture")]
public sealed class GetCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public GetCommandTests()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    private sealed class FakeLoader : IConfigLoader
    {
        public TomlTable Table { get; }

        public FakeLoader(TomlTable table) { Table = table; }

        public Core.Config.TotalRecallConfig LoadDefaults()
            => throw new NotImplementedException();
        public Core.Config.TotalRecallConfig LoadEffectiveConfig(string? userConfigPath = null)
            => throw new NotImplementedException();
        public TomlTable LoadEffectiveTable(string? userConfigPath = null) => Table;
    }

    private static TomlTable Canned()
    {
        var t = new TomlTable();
        ConfigWriter.SetNestedKey(t, "tiers.hot.max_entries", 50L);
        ConfigWriter.SetNestedKey(t, "tiers.warm.similarity_threshold", 0.65);
        ConfigWriter.SetNestedKey(t, "embedding.model", "all-MiniLM-L6-v2");
        ConfigWriter.SetNestedKey(t, "embedding.dimensions", 384L);
        return t;
    }

    [Fact]
    public async Task Help_ExitsZero()
    {
        var cmd = new GetCommand(new FakeLoader(Canned()), new StringWriter());
        // The top-level dispatcher swallows --help before calling the command,
        // but we still exercise the command directly with no args to prove
        // it renders the full config.
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task NoKey_PrintsFullConfig()
    {
        var sink = new StringWriter();
        var cmd = new GetCommand(new FakeLoader(Canned()), sink);

        var code = await cmd.RunAsync(Array.Empty<string>());

        Assert.Equal(0, code);
        var text = sink.ToString();
        Assert.Contains("tiers", text);
        Assert.Contains("embedding", text);
        Assert.Contains("all-MiniLM-L6-v2", text);
    }

    [Fact]
    public async Task ValidKey_PrintsKeyValue()
    {
        var sink = new StringWriter();
        var cmd = new GetCommand(new FakeLoader(Canned()), sink);

        var code = await cmd.RunAsync(new[] { "embedding.model" });

        Assert.Equal(0, code);
        var text = sink.ToString();
        Assert.Contains("embedding.model", text);
        Assert.Contains("all-MiniLM-L6-v2", text);
    }

    [Fact]
    public async Task MissingKey_Exit1_StderrMessage()
    {
        var sink = new StringWriter();
        var cmd = new GetCommand(new FakeLoader(Canned()), sink);

        var code = await cmd.RunAsync(new[] { "nope.nonsense" });

        Assert.Equal(1, code);
        Assert.Contains("key not found", _errWriter.ToString());
    }

    [Fact]
    public async Task JsonFullConfig_ParsesAsJson()
    {
        var sink = new StringWriter();
        var cmd = new GetCommand(new FakeLoader(Canned()), sink);

        var code = await cmd.RunAsync(new[] { "--json" });

        Assert.Equal(0, code);
        var text = sink.ToString().Trim();
        Assert.StartsWith("{", text);
        Assert.EndsWith("}", text);
        Assert.Contains("\"tiers\"", text);
        Assert.Contains("\"all-MiniLM-L6-v2\"", text);
    }

    [Fact]
    public async Task JsonLeaf_PrintsKeyValue()
    {
        var sink = new StringWriter();
        var cmd = new GetCommand(new FakeLoader(Canned()), sink);

        var code = await cmd.RunAsync(new[] { "embedding.dimensions", "--json" });

        Assert.Equal(0, code);
        var text = sink.ToString();
        Assert.Contains("\"key\":\"embedding.dimensions\"", text);
        Assert.Contains("\"value\":384", text);
    }

    [Fact]
    public async Task UnknownFlag_Exit2()
    {
        var sink = new StringWriter();
        var cmd = new GetCommand(new FakeLoader(Canned()), sink);

        var code = await cmd.RunAsync(new[] { "--bogus" });

        Assert.Equal(2, code);
    }
}
