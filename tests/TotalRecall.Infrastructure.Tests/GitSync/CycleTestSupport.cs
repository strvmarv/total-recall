using System.Text.RegularExpressions;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// A cycle test's world: a <see cref="PushRig"/> whose configured remote is the bare remote's own address (so a clone the
/// cycle makes really clones it), with the guard, paths, store and embedder the cycle needs and helpers to look at the
/// result. <see cref="Cycle"/> builds a <see cref="GitSyncCycle"/> that records its <c>OnStep</c> names in <see cref="Steps"/>.
/// </summary>
internal sealed partial class CycleRig : IDisposable
{
    public static readonly DateTimeOffset StartTime = new(2026, 11, 19, 1, 2, 3, 456, TimeSpan.Zero);

    public PushRig Push { get; } = new(configuredRemote: true);
    public PullRig Pull => Push.Pull;
    public List<string> Steps { get; } = [];
    public DateTimeOffset Now { get; set; } = StartTime;

    public CycleSettings Settings(TimeSpan? lockWait = null, string? address = null) =>
        new(address ?? Pull.RemoteAddress, "main", PushRig.Label, 0, lockWait ?? TimeSpan.Zero);

    public GitSyncCycle Cycle(CycleSettings? settings = null)
    {
        var cycle = new GitSyncCycle(
            Pull.Client, Pull.Guard, Pull.Store, Push.Embedder, Pull.Paths, settings ?? Settings(), () => Now, () => Push.Now);
        cycle.OnStep = Steps.Add;
        return cycle;
    }

    public Task<CycleOutcome> Run(CancellationToken ct = default) => Cycle().RunAsync(ct);

    // ---- the recorded values ----

    public string? LastOk => Pull.Store.GetMeta(SyncIdentity.MetaKeys.LastOk);
    public string? LastError => Pull.Store.GetMeta(SyncIdentity.MetaKeys.LastError);

    /// <summary>Matches a recorded error line: UTC time, step, kind.</summary>
    public static Regex ErrorLine(string step, string kind) =>
        new($@"^\d{{4}}-\d{{2}}-\d{{2}}T\d{{2}}:\d{{2}}:\d{{2}}\.\d{{3}}Z {Regex.Escape(step)} {Regex.Escape(kind)}:");

    /// <summary>The commands of the network kinds recorded so far (clone, ls-remote, fetch, push).</summary>
    public int NetworkCalls() => Pull.Count(c => c.IsClone || c.IsLsRemote || c.IsFetch || c.IsPush);

    public int PushCalls() => Pull.Count(c => c.IsPush);

    // ---- the lock and the clone ----

    public async Task<SyncLockHandle> HoldLock() =>
        await SyncLock.TryAcquireAsync(Pull.Paths.LockPath, TimeSpan.Zero) ?? throw new InvalidOperationException("the lock is already held");

    public string PlantIndexLock()
    {
        var path = Path.Combine(Pull.Paths.CloneDirectory, ".git", "index.lock");
        File.WriteAllText(path, "");
        return path;
    }

    public void DeleteClone() => GitTestEnvironment.ForceDelete(Pull.Paths.CloneDirectory);

    /// <summary>Breaks the clone's <c>.git</c> so git cannot open it as a repository, while the directories stay real.</summary>
    public void CorruptClone()
    {
        var git = Path.Combine(Pull.Paths.CloneDirectory, ".git");
        File.WriteAllText(Path.Combine(git, "HEAD"), "garbage, not a ref\n");
        foreach (var name in new[] { "objects", "refs" })
        {
            var directory = Path.Combine(git, name);
            if (Directory.Exists(directory))
                GitTestEnvironment.ForceDelete(directory);
        }
    }

    /// <summary>The directories beside the clone that a sideline made (<c>repo.broken-*</c>, <c>repo.partial-*</c>).</summary>
    public string[] Sidelined(string label)
    {
        var parent = Path.GetDirectoryName(Pull.Paths.CloneDirectory)!;
        return Directory.Exists(parent) ? Directory.GetDirectories(parent, $"repo.{label}-*") : [];
    }

    public void Dispose() => Push.Dispose();
}
