using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// One recorder shared by every test of the git client: when the collection ends, it asserts that no git invocation of
/// the whole run could force, delete, mirror or prune.
/// </summary>
public sealed class RecordingFixture : IDisposable
{
    internal RecordingGitProcess Recorder { get; } = new(new GitProcess());

    public void Dispose() => ForceFlags.AssertNone(Recorder.Argvs);
}

[CollectionDefinition("git-client", DisableParallelization = true)]
public sealed class GitClientCollection : ICollectionFixture<RecordingFixture>
{
}

/// <summary>A client over a throwaway data directory, with the user's global configuration redirected.</summary>
internal sealed class ClientRig : IDisposable
{
    public string Root { get; }
    public GitTestEnvironment Env { get; } = new();

    /// <summary>A clean home for the test's own assertions, never the (possibly hostile) one the client uses.</summary>
    public GitTestEnvironment Clean { get; } = new();

    public MarkerFiles? Markers { get; }
    public string DbPath { get; }
    public GitSyncPaths Paths { get; }
    public GitClient Client { get; }
    public RecordingGitProcess Recorder { get; }

    public ClientRig(
        RecordingFixture fixture,
        string remoteName = "https://example.invalid/o/r.git",
        bool hostile = false,
        string? dbPath = null,
        Func<GitClientOptions, GitClientOptions>? options = null,
        string? executable = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "tr-rig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        if (hostile)
            Markers = Env.WriteHostileConfig();
        else
            Env.WriteGitConfig("");
        DbPath = dbPath ?? Path.Combine(Root, "data", "total-recall.db");
        Paths = GitSyncPaths.For(DbPath, remoteName, caseInsensitive: false);
        Recorder = fixture.Recorder;
        var clientOptions = new GitClientOptions { EnvironmentOverrides = Env.EnvironmentOverrides };
        if (options is not null)
            clientOptions = options(clientOptions);
        Client = new GitClient(Recorder, executable ?? GitExecutable.TryFind() ?? "git", Paths, clientOptions);
    }

    public string CloneDirectory => Paths.CloneDirectory;

    /// <summary>Runs the real git in the clone with the clean home.</summary>
    public string Git(params string[] args) => Clean.RunGit(CloneDirectory, args);

    /// <summary>Loose plus packed objects in the clone.</summary>
    public int ObjectCount()
    {
        var total = 0;
        foreach (var line in Git("count-objects", "-v").Split('\n'))
        {
            if (line.StartsWith("count:", StringComparison.Ordinal) || line.StartsWith("in-pack:", StringComparison.Ordinal))
                total += int.Parse(line[(line.IndexOf(':') + 1)..].Trim());
        }
        return total;
    }

    public void Dispose()
    {
        Env.Dispose();
        Clean.Dispose();
        try
        {
            GitTestEnvironment.ForceDelete(Root);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class OutcomeAssert
{
    public static T Ok<T>(GitOutcome<T> outcome)
    {
        Assert.True(outcome.IsOk, outcome.IsOk ? "" : $"failed: {outcome.Failure.Failure} exit {outcome.Failure.ExitCode}: {outcome.Failure.StderrText}");
        return outcome.Value;
    }

    public static void Failed<T>(GitOutcome<T> outcome, SyncGit.FailureKind kind)
    {
        Assert.False(outcome.IsOk, "expected a failure");
        Assert.Equal(kind, outcome.Failure.Failure);
    }
}
