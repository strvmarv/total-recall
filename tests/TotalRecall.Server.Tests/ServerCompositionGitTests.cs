// Milestone 1a-2 — the `git` storage mode: opens as the local store with no git command and no network, and falls
// back with `sqlite (git failed)` when a sync cannot work.

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Linq;
using TotalRecall.Infrastructure.Config;
using Xunit;

[CollectionDefinition("process-environment-console", DisableParallelization = true)]
public sealed class ProcessEnvironmentConsoleCollection;

[Collection("process-environment-console")]
public sealed class ServerCompositionGitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-open-git-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevHome = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
    private readonly TextWriter _prevErr = Console.Error;
    private readonly StringWriter _err = new();
    private readonly string _gitDir;

    public ServerCompositionGitTests()
    {
        Directory.CreateDirectory(_root);
        // OpenSqlite creates the data directory even when a database path is given: keep it out of the real home.
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", Path.Combine(_root, "home"));
        Console.SetError(_err);
        _gitDir = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_gitDir);
        File.WriteAllText(Path.Combine(_gitDir, OperatingSystem.IsWindows() ? "git.exe" : "git"), "");
    }

    public void Dispose()
    {
        Console.SetError(_prevErr);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", _prevHome);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Db(string name) => Path.Combine(_root, name + ".db");

    private Core.Config.TotalRecallConfig Config(string toml)
    {
        var path = Path.Combine(_root, "config-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(path, toml);
        return new ConfigLoader().LoadEffectiveConfig(path);
    }

    private ServerCompositionHandles Open(string toml, string? pathVariable = null) =>
        ServerComposition.OpenConfigured(Config(toml), Db(Guid.NewGuid().ToString("N")), "assistant", pathVariable ?? _gitDir);

    private const string GitMode = "[storage]\nmode = \"git\"\n";

    [Fact]
    public void AUsableConfiguration_OpensAsTheLocalStoreWithTheLabelGit()
    {
        using var git = Open(GitMode + "[git_sync]\nremote = \"git@github.com:o/r.git\"\n");
        using var local = Open("[storage]\nmode = \"local\"\n");

        Assert.Equal("git", git.StorageMode);
        Assert.Equal(
            local.Registry.ListTools().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal),
            git.Registry.ListTools().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("", _err.ToString());
    }

    [Fact]
    public void ACorrectedSetting_IsWrittenOnceToTheErrorOutput()
    {
        using var git = Open(GitMode + "[git_sync]\nremote = \"git@github.com:o/r.git\"\ninterval_seconds = 5\n");

        Assert.Equal("git", git.StorageMode);
        var lines = _err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains("interval_seconds", lines[0]);
    }

    [Theory]
    [InlineData("[storage]\nmode = \"git\"\n", "remote")]
    [InlineData("[storage]\nmode = \"git\"\n[git_sync]\nremote = \"ext::sh\"\n", "repository address")]
    [InlineData("[storage]\nmode = \"git\"\n[git_sync]\nremote = \"git@github.com:o/r.git\"\nbranch = \".bad\"\n", "branch")]
    public void AnUnusableConfiguration_FallsBackWithTheReason(string toml, string reasonFragment)
    {
        using var handles = Open(toml);

        Assert.Equal("sqlite (git failed)", handles.StorageMode);
        var text = _err.ToString();
        Assert.Contains("[total-recall] git storage failed, falling back to sqlite:", text);
        Assert.Contains(reasonFragment, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoGitOnThePath_FallsBack()
    {
        var emptyDir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyDir);

        using var handles = Open(GitMode + "[git_sync]\nremote = \"git@github.com:o/r.git\"\n", pathVariable: emptyDir);

        Assert.Equal("sqlite (git failed)", handles.StorageMode);
        Assert.Contains("git executable", _err.ToString());
    }

    [Fact]
    public void ARefusedRemote_NeverLeaksItsCredentials()
    {
        using var handles = Open(GitMode + "[git_sync]\nremote = \"ext::https://user:tok3n12345@h/x\"\n");

        Assert.Equal("sqlite (git failed)", handles.StorageMode);
        Assert.DoesNotContain("tok3n12345", _err.ToString());
    }

    [Fact]
    public void ALocalMode_IsUnchanged()
    {
        using var handles = Open("[storage]\nmode = \"local\"\n");

        Assert.Equal("sqlite", handles.StorageMode);
    }
}
