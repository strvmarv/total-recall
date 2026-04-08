using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands;
using TotalRecall.Infrastructure.Migration;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

public sealed class MigrateCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter;
    private readonly StringWriter _errWriter;

    public MigrateCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-migcmd-" + Guid.NewGuid().ToString("N"));
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
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private sealed class StubMigrator : IMigrateCommand
    {
        public MigrationResult Result { get; init; } =
            new(Success: true, EntriesMigrated: 3, ErrorMessage: null);
        public int CallCount { get; private set; }
        public string? LastSource { get; private set; }
        public string? LastTarget { get; private set; }

        public Task<MigrationResult> MigrateAsync(
            string sourceDbPath, string targetDbPath, CancellationToken ct)
        {
            CallCount++;
            LastSource = sourceDbPath;
            LastTarget = targetDbPath;
            return Task.FromResult(Result);
        }
    }

    private string NewFakeSource()
    {
        var path = Path.Combine(_tempDir, "src-" + Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllBytes(path, new byte[] { 0x01 });
        return path;
    }

    [Fact]
    public async Task MissingFrom_ReturnsExit2()
    {
        var cmd = new MigrateCommand(new StubMigrator());
        var code = await cmd.RunAsync(new[] { "--to", Path.Combine(_tempDir, "t.db") });
        Assert.Equal(2, code);
        Assert.Contains("--from", _errWriter.ToString());
    }

    [Fact]
    public async Task MissingTo_ReturnsExit2()
    {
        var cmd = new MigrateCommand(new StubMigrator());
        var code = await cmd.RunAsync(new[] { "--from", NewFakeSource() });
        Assert.Equal(2, code);
        Assert.Contains("--to", _errWriter.ToString());
    }

    [Fact]
    public async Task FromFileMissing_ReturnsExit1()
    {
        var cmd = new MigrateCommand(new StubMigrator());
        var code = await cmd.RunAsync(new[]
        {
            "--from", Path.Combine(_tempDir, "nope.db"),
            "--to", Path.Combine(_tempDir, "t.db"),
        });
        Assert.Equal(1, code);
        Assert.Contains("does not exist", _errWriter.ToString());
    }

    [Fact]
    public async Task TargetExistsWithoutForce_ReturnsExit2()
    {
        var src = NewFakeSource();
        var tgt = Path.Combine(_tempDir, "tgt.db");
        File.WriteAllText(tgt, "x");
        var stub = new StubMigrator();
        var cmd = new MigrateCommand(stub);
        var code = await cmd.RunAsync(new[] { "--from", src, "--to", tgt });
        Assert.Equal(2, code);
        Assert.Equal(0, stub.CallCount);
        Assert.Contains("--force", _errWriter.ToString());
    }

    [Fact]
    public async Task TargetExistsWithForce_DeletesAndInvokes()
    {
        var src = NewFakeSource();
        var tgt = Path.Combine(_tempDir, "tgt.db");
        File.WriteAllText(tgt, "x");
        var stub = new StubMigrator
        {
            Result = new MigrationResult(true, 7, null),
        };
        var cmd = new MigrateCommand(stub);
        var code = await cmd.RunAsync(new[] { "--from", src, "--to", tgt, "--force" });
        Assert.Equal(0, code);
        Assert.Equal(1, stub.CallCount);
        Assert.Contains("7 entries", _outWriter.ToString());
    }

    [Fact]
    public async Task HappyPath_InvokesMigratorAndPrintsSummary()
    {
        var src = NewFakeSource();
        var tgt = Path.Combine(_tempDir, "tgt.db");
        var stub = new StubMigrator
        {
            Result = new MigrationResult(true, 42, null),
        };
        var cmd = new MigrateCommand(stub);
        var code = await cmd.RunAsync(new[] { "--from", src, "--to", tgt });
        Assert.Equal(0, code);
        Assert.Equal(1, stub.CallCount);
        Assert.Equal(src, stub.LastSource);
        Assert.Equal(tgt, stub.LastTarget);
        var stdout = _outWriter.ToString();
        Assert.Contains("migrated 42 entries", stdout);
    }

    [Fact]
    public async Task MigrationFailureResult_ReturnsExit1()
    {
        var src = NewFakeSource();
        var tgt = Path.Combine(_tempDir, "tgt.db");
        var stub = new StubMigrator
        {
            Result = new MigrationResult(false, 0, "boom"),
        };
        var cmd = new MigrateCommand(stub);
        var code = await cmd.RunAsync(new[] { "--from", src, "--to", tgt });
        Assert.Equal(1, code);
        Assert.Contains("boom", _errWriter.ToString());
    }

    [Fact]
    public async Task UnknownArg_ReturnsExit2()
    {
        var cmd = new MigrateCommand(new StubMigrator());
        var code = await cmd.RunAsync(new[] { "--what", "xyz" });
        Assert.Equal(2, code);
    }

    // NOTE: Top-level --help dispatch for 'migrate' is already covered by
    // CliAppTests against a stub registry. Calling CliApp.Run directly here
    // would race with CliAppTests' SetRegistryForTestsInternal overrides
    // under xunit's per-class parallelism.
}
