using System;
using System.IO;
using TotalRecall.Infrastructure.Eval;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

/// <summary>
/// EvalPaths resolves the bundled eval corpus/benchmark relative to the binary
/// location (AppContext.BaseDirectory) rather than the process CWD, so the
/// retrieval benchmark works regardless of where the host process was launched
/// (e.g. the Web UI server, whose CWD is not the package root).
/// </summary>
public sealed class EvalPathsTests : IDisposable
{
    private readonly string _root;

    public EvalPathsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"evalpaths-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ResolveFrom_WalksUpToFindBundledEvalFile()
    {
        // Layout: <root>/eval/corpus/memories.jsonl exists; the "binary" dir is
        // several levels below <root>. Resolving from there must walk up and
        // return the real bundled file, not a CWD-relative path.
        var bundled = Path.Combine(_root, "eval", "corpus", "memories.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllText(bundled, "{}\n");

        var binDir = Path.Combine(_root, "binaries", "win-x64");
        Directory.CreateDirectory(binDir);

        var resolved = EvalPaths.ResolveFrom(binDir, "corpus", "memories.jsonl");

        Assert.True(Path.IsPathRooted(resolved));
        Assert.Equal(Path.GetFullPath(bundled), Path.GetFullPath(resolved));
    }

    [Fact]
    public void ResolveFrom_FallsBackToRelative_WhenNoEvalDirAbove()
    {
        // No eval/ anywhere up the tree -> last-resort relative path, matching
        // the historical EvalGrowHandler.ResolveDefaultBenchmarkPath behavior.
        var binDir = Path.Combine(_root, "binaries", "win-x64");
        Directory.CreateDirectory(binDir);

        var resolved = EvalPaths.ResolveFrom(binDir, "benchmarks", "retrieval.jsonl");

        Assert.Equal(Path.Combine("eval", "benchmarks", "retrieval.jsonl"), resolved);
    }

    [Fact]
    public void ResolveFrom_Throws_OnEmptySegments()
    {
        // Without segments the result would silently collapse to "eval" and
        // mis-resolve. Reject it at the API boundary instead.
        Assert.Throws<ArgumentException>(() => EvalPaths.ResolveFrom(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveFrom_Throws_OnMissingStartDir(string? startDir)
    {
        // ArgumentNullException (null) / ArgumentException (empty) both derive
        // from ArgumentException.
        Assert.ThrowsAny<ArgumentException>(
            () => EvalPaths.ResolveFrom(startDir!, "corpus", "memories.jsonl"));
    }
}
