using System.Text;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class SyncSetupJoinTests
{
    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>A real sync repository: an empty bare repository that another machine initialised.</summary>
    private static async Task<(BareRemote Remote, string RepoId)> NewSyncRepository()
    {
        var remote = BareRemote.Create(0);
        using var initRig = new SetupRig();
        var result = await initRig.Service().InitAsync(remote.Url, null);
        Assert.Equal(0, result.ExitCode);
        return (remote, ManifestId(remote));
    }

    private static string ManifestId(BareRemote remote) =>
        ManifestFile.Parse(Encoding.UTF8.GetBytes(remote.Git("show", "main:manifest.json"))).Manifest!.RepoId;

    private static string? Meta(SetupRig rig, string key)
    {
        using var conn = SyncDb.OpenMigrated(rig.DbPath);
        return SyncDb.Meta(conn, key);
    }

    private static void Exec(SetupRig rig, string sql)
    {
        using var conn = SyncDb.OpenMigrated(rig.DbPath);
        SyncDb.Exec(conn, sql);
    }

    private static long Count(SetupRig rig, string table)
    {
        using var conn = SyncDb.OpenMigrated(rig.DbPath);
        return SyncDb.Count(conn, table);
    }

    private static void SeedBookkeeping(SetupRig rig)
    {
        Exec(rig, "INSERT INTO git_sync_entry (entry_id, state) VALUES ('e1','active')");
        Exec(rig, "INSERT INTO git_sync_review (entry_id, kind, state, arrived_at) VALUES ('e1','new-pin','open',1), ('e1','conflict','open',1)");
        Exec(rig, "INSERT INTO hot_memories (id, content, created_at, updated_at, last_accessed_at) VALUES ('orphan','kept',1,1,1)");
    }

    private static string Clone(SetupRig rig, string address) => rig.PathsFor(address).CloneDirectory;

    private static string Protect(SetupRig rig, string address)
    {
        var clone = Clone(rig, address);
        var sha = rig.GitEnv.RunGit(clone, "rev-parse", "HEAD").Trim();
        rig.GitEnv.RunGit(clone, "update-ref", "refs/total-recall/last-synced", sha);
        return sha;
    }

    private static string ProtectiveRef(SetupRig rig, string clone) =>
        rig.GitEnv.RunGit(clone, "for-each-ref", "--format=%(objectname)", "refs/total-recall").Trim();

    /// <summary>The sidelined directories beside a clone (<c>repo.left-...</c>); a glob like <c>repo.*</c> would also match <c>repo</c> itself.</summary>
    private static IEnumerable<string> Siblings(string clone, string pattern)
    {
        var prefix = pattern.TrimEnd('*');
        return Directory.GetDirectories(Path.GetDirectoryName(clone)!).Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.Ordinal));
    }

    private static async Task<bool> IsJoinedAsync(SetupRig rig, string address, string branch = "main")
    {
        using var conn = SyncDb.OpenMigrated(rig.DbPath);
        var store = new SqliteStore(conn);
        var client = new GitClient(new GitProcess(), GitExecutable.TryFind()!, rig.PathsFor(address), new GitClientOptions { EnvironmentOverrides = rig.GitEnv.EnvironmentOverrides });
        var guard = new RepoGuard(GuardSettings.For(address, branch), store, client, rig.Host, rig.Probe, () => rig.Now);
        var verdict = await guard.EvaluateIdentityAsync();
        return verdict.Join.IsJoined && verdict.CanSync;
    }

    private static void AssertNoCleanAndNoForce(SetupRig rig) =>
        Assert.All(rig.Recorder.Calls, c => Assert.False(c.Command.IsClean, "a clean command ran"));

    // ---- the first join -----------------------------------------------------------------------------------------

    [GitFact]
    public async Task Join_TheFirstTime_ClonesReadsTheManifestAndRecordsTheIdentity()
    {
        var (remote, repoId) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig { MachineIdText = "machine-two" };

        var result = await rig.Service().JoinAsync(remote.Url, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(repoId, Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Equal(remote.Url, Meta(rig, SyncIdentity.MetaKeys.Remote));
        Assert.Equal(rig.Host.Fingerprint, Meta(rig, SyncIdentity.MetaKeys.Host));
        Assert.Matches("^[0-9a-f]{8}$", Meta(rig, SyncIdentity.MetaKeys.Machine));
        Assert.Equal(2, rig.Config.Renders.Count);
        Assert.Equal(
            [("storage.mode", (object)"git"), ("git_sync.remote", remote.Url), ("git_sync.branch", "main")],
            rig.Config.Renders[^1]);
        Assert.Single(rig.Config.Writes);
        Assert.True(await IsJoinedAsync(rig, remote.Url));
        Assert.Equal("", ProtectiveRef(rig, Clone(rig, remote.Url)));
        Assert.Contains(result.Lines, l => l.Contains("not part of this build yet"));
        AssertNoCleanAndNoForce(rig);
    }

    [GitFact]
    public async Task Join_AMachineThatJoinsAfterAnInit_GetsItsOwnMachineId()
    {
        var remote = BareRemote.Create(0);
        using var _ = remote;
        using var first = new SetupRig { MachineIdText = "machine-one" };
        Assert.Equal(0, (await first.Service().InitAsync(remote.Url, null)).ExitCode);
        using var second = new SetupRig { MachineIdText = "machine-two" };

        Assert.Equal(0, (await second.Service().JoinAsync(remote.Url, null)).ExitCode);

        Assert.NotEqual(Meta(first, SyncIdentity.MetaKeys.Machine), Meta(second, SyncIdentity.MetaKeys.Machine));
        Assert.Equal(Meta(first, SyncIdentity.MetaKeys.RepoId), Meta(second, SyncIdentity.MetaKeys.RepoId));
    }

    // ---- refusals ---------------------------------------------------------------------------------------------

    [GitFact]
    public async Task Join_RefusesWhatIsNotAJoinableRepository_AndRecordsNothing()
    {
        // nothing to join: empty, or no such branch
        using (var empty = BareRemote.Create(0))
        {
            using var rig = new SetupRig();
            Assert.Equal(SyncSetup.Refusal.NothingToJoin, (await rig.Service().JoinAsync(empty.Url, null)).Refusal);
            Assert.Empty(rig.Config.Writes);
        }
        using (var other = BareRemote.Create(0))
        {
            other.AddRef("refs/heads/dev", other.AddEmptyCommit());
            using var rig = new SetupRig();
            Assert.Equal(SyncSetup.Refusal.NothingToJoin, (await rig.Service().JoinAsync(other.Url, null)).Refusal);
        }

        var cases = new (Action<BareRemote> Prepare, SyncSetup.Refusal Expect)[]
        {
            (_ => { }, SyncSetup.Refusal.NotASyncRepo), // a repository with a main branch and no manifest
            (r => r.PushCommit("manifest.json", "{\"format\":\"total-recall/v1\",\"format\":\"total-recall/v1\"}"), SyncSetup.Refusal.InvalidManifest),
            (r => r.PushCommit("manifest.json", "{\"format\":\"total-recall/v2\"}"), SyncSetup.Refusal.FormatTooNew),
        };
        foreach (var (prepare, expect) in cases)
        {
            using var remote = BareRemote.Create();
            prepare(remote);
            using var rig = new SetupRig();

            var result = await rig.Service().JoinAsync(remote.Url, null);

            Assert.Equal(expect, result.Refusal);
            Assert.Null(Meta(rig, SyncIdentity.MetaKeys.RepoId));
            Assert.Empty(rig.Config.Writes);
            Assert.True(Directory.Exists(Path.Combine(Clone(rig, remote.Url), ".git")), "the clone is a cache and stays");
        }
    }

    [GitFact]
    public async Task Join_ATooLongListing_IsFailed_NotNotEmpty()
    {
        using var remote = BareRemote.Create();
        using var rig = new SetupRig();
        rig.Fault = c => c.IsLsRemote ? FaultingGitProcess.Failure(SyncGit.FailureKind.OutputTooLarge) : null;
        Assert.Equal(SyncSetup.Refusal.Failed, (await rig.Service().JoinAsync(remote.Url, null)).Refusal);
    }

    // ---- a second join of the same repository -------------------------------------------------------------------

    [GitFact]
    public async Task Join_ASecondTime_RefreshesTheClone_AndKeepsTheRowsAndTheIds()
    {
        var (remote, repoId) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        SeedBookkeeping(rig);
        var machine = Meta(rig, SyncIdentity.MetaKeys.Machine);
        var clone = Clone(rig, remote.Url);
        var sha = Protect(rig, remote.Url);
        var stale = Path.Combine(clone, ".git", "index.lock");
        File.WriteAllText(stale, "");
        var before = rig.Recorder.Calls.Count;

        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);

        Assert.False(File.Exists(stale), "the stale lock was not removed");
        var commands = rig.Recorder.Calls.Skip(before).Select(c => c.Command).ToList();
        Assert.DoesNotContain(commands, c => c.IsClone);
        Assert.DoesNotContain(commands, c => c.IsClean);
        var order = commands.Select(c => c.IsSetOriginUrl ? "origin" : c.IsFetch ? "fetch" : c.IsResetHard ? "reset" : "").Where(x => x != "").ToList();
        Assert.Equal(new[] { "origin", "fetch", "reset" }, order);
        Assert.Equal(repoId, Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Equal(machine, Meta(rig, SyncIdentity.MetaKeys.Machine));
        Assert.Equal(1, Count(rig, "git_sync_entry"));
        Assert.Equal(2, Count(rig, "git_sync_review"));
        Assert.Equal(sha, ProtectiveRef(rig, clone));
        Assert.True(!Siblings(clone, "repo.*").Any(), string.Join(",", Siblings(clone, "repo.*")));
    }

    [GitFact]
    public async Task Join_ARepositoryRecreatedAtTheSameAddress_DropsTheOldProtectiveRef()
    {
        var (remote, oldId) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        SeedBookkeeping(rig);
        var clone = Clone(rig, remote.Url);
        Protect(rig, remote.Url);
        Assert.NotEqual("", ProtectiveRef(rig, clone));

        remote.Recreate();
        using (var third = new SetupRig { MachineIdText = "machine-three", Seed = 7 })
            Assert.Equal(0, (await third.Service().InitAsync(remote.Url, null)).ExitCode);
        var newId = ManifestId(remote);
        Assert.NotEqual(oldId, newId);

        var result = await rig.Service().JoinAsync(remote.Url, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(newId, Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Single(Siblings(clone, "repo.left-*"));
        Assert.Contains(result.Lines, l => l.StartsWith("note: the earlier clone of this address was kept at", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, l => l.StartsWith("note: a clone was kept at", StringComparison.Ordinal));
        Assert.True(ProtectiveRef(rig, clone) == "", "the new clone carries the old repository's protective ref");
        Assert.Equal(0, Count(rig, "git_sync_entry"));
        Assert.Equal(1, Count(rig, "git_sync_review")); // the conflict row stays
        Assert.Equal(1, Count(rig, "hot_memories"));
        Assert.True(await IsJoinedAsync(rig, remote.Url));
    }

    [GitFact]
    public async Task Join_TheSameRepositoryUnderARespelledAddress_KeepsTheStateAndMovesTheClone()
    {
        var (remote, repoId) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        SeedBookkeeping(rig);
        var machine = Meta(rig, SyncIdentity.MetaKeys.Machine);
        var oldClone = Clone(rig, remote.Url);
        var sha = Protect(rig, remote.Url);
        var newAddress = remote.Path; // a plain path for the same bare repository
        var newClone = Clone(rig, newAddress);

        var result = await rig.Service().JoinAsync(newAddress, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(repoId, Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Equal(machine, Meta(rig, SyncIdentity.MetaKeys.Machine));
        Assert.Equal(newAddress, Meta(rig, SyncIdentity.MetaKeys.Remote));
        Assert.Equal(1, Count(rig, "git_sync_entry"));
        Assert.False(Directory.Exists(oldClone), "the old clone was not moved");
        Assert.Equal(sha, ProtectiveRef(rig, newClone));
        Assert.Equal(newAddress, rig.GitEnv.RunGit(newClone, "config", "remote.origin.url").Trim());
        Assert.Single(Siblings(newClone, "repo.superseded-*"));
        Assert.True(await IsJoinedAsync(rig, newAddress));
    }

    [GitFact]
    public async Task Join_AMovedDataDirectory_KeepsTheRowsAndTheRef_ButTakesANewMachineId()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        SeedBookkeeping(rig);
        var machine = Meta(rig, SyncIdentity.MetaKeys.Machine);
        var clone = Clone(rig, remote.Url);
        var sha = Protect(rig, remote.Url);
        Exec(rig, $"UPDATE _meta SET value = 'a-different-host' WHERE key = '{SyncIdentity.MetaKeys.Host}'");

        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);

        Assert.Equal(rig.Host.Fingerprint, Meta(rig, SyncIdentity.MetaKeys.Host));
        Assert.NotEqual(machine, Meta(rig, SyncIdentity.MetaKeys.Machine));
        Assert.Matches("^[0-9a-f]{8}$", Meta(rig, SyncIdentity.MetaKeys.Machine));
        Assert.Equal(1, Count(rig, "git_sync_entry"));
        Assert.Equal(sha, ProtectiveRef(rig, clone));
    }

    [GitFact]
    public async Task Join_AMissingStoredMachineId_GetsANewOne()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        Exec(rig, $"DELETE FROM _meta WHERE key = '{SyncIdentity.MetaKeys.Machine}'");

        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);

        Assert.Matches("^[0-9a-f]{8}$", Meta(rig, SyncIdentity.MetaKeys.Machine));
    }

    [GitFact]
    public async Task Join_ADifferentBranch_OrARefusedConfiguredBranch_CountsAsADifferentRepository()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        remote.AddRef("refs/heads/dev", remote.Git("rev-parse", "main").Trim());

        using (var rig = new SetupRig())
        {
            Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
            SeedBookkeeping(rig);
            var machine = Meta(rig, SyncIdentity.MetaKeys.Machine);
            rig.Settings = GuardSettings.For(remote.Url, "main"); // the configured branch is main

            Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, "dev")).ExitCode);

            Assert.Equal(0, Count(rig, "git_sync_entry"));
            Assert.NotEqual(machine, Meta(rig, SyncIdentity.MetaKeys.Machine));
            Assert.Single(Siblings(Clone(rig, remote.Url), "repo.left-*"));
        }
        using (var rig = new SetupRig())
        {
            Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
            SeedBookkeeping(rig);
            rig.Settings = GuardSettings.For(null, "not a branch!"); // refused: Branch = None

            Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, "main")).ExitCode);

            Assert.Equal(0, Count(rig, "git_sync_entry"));
        }
    }

    // ---- fetch failures ---------------------------------------------------------------------------------------

    [GitFact]
    public async Task Join_ATransientFetchFailure_KeepsTheClone()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        var cases = new (SyncGit.FailureKind Kind, SyncSetup.Refusal Expect)[]
        {
            (SyncGit.FailureKind.Network, SyncSetup.Refusal.Unreachable),
            (SyncGit.FailureKind.Auth, SyncSetup.Refusal.NeedsAuth),
            (SyncGit.FailureKind.TimedOut, SyncSetup.Refusal.Unreachable),
            (SyncGit.FailureKind.RemoteNotFound, SyncSetup.Refusal.Unreachable),
            (SyncGit.FailureKind.RemoteBranchMissing, SyncSetup.Refusal.NothingToJoin),
            (SyncGit.FailureKind.OutputTooLarge, SyncSetup.Refusal.Failed),
            (SyncGit.FailureKind.NonFastForward, SyncSetup.Refusal.Failed),
            (SyncGit.FailureKind.Cancelled, SyncSetup.Refusal.Failed),
        };
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        var clone = Clone(rig, remote.Url);
        var sha = Protect(rig, remote.Url);
        File.WriteAllText(Path.Combine(clone, "marker.txt"), "mine");

        foreach (var (kind, expect) in cases)
        {
            rig.Fault = c => c.IsFetch ? FaultingGitProcess.Failure(kind) : null;
            var result = await rig.Service().JoinAsync(remote.Url, null);
            Assert.Equal(expect, result.Refusal);
            Assert.Equal(sha, ProtectiveRef(rig, clone));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(clone, "marker.txt")));
            Assert.True(!Siblings(clone, "repo.*").Any(), string.Join(",", Siblings(clone, "repo.*")));
        }
    }

    [GitFact]
    public async Task Join_AFetchFailureOfKindOther_KeepsASoundClone()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        var clone = Clone(rig, remote.Url);
        var sha = Protect(rig, remote.Url);
        rig.Fault = c => c.IsFetch ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "fatal: unable to access 'x': The requested URL returned error: 503") : null;

        var result = await rig.Service().JoinAsync(remote.Url, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("the fetch failed", result.RefusalText);
        Assert.Equal(sha, ProtectiveRef(rig, clone));
        Assert.True(!Siblings(clone, "repo.*").Any(), string.Join(",", Siblings(clone, "repo.*")));
    }

    [GitFact]
    public async Task Join_AFetchFailureOfKindOther_WithACancelledProbe_KeepsTheClone()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        var clone = Clone(rig, remote.Url);
        rig.Fault = c => c.IsFetch ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other) : c.IsRevParse ? FaultingGitProcess.Failure(SyncGit.FailureKind.Cancelled) : null;

        var result = await rig.Service().JoinAsync(remote.Url, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.True(!Siblings(clone, "repo.*").Any(), string.Join(",", Siblings(clone, "repo.*")));
    }

    [GitFact]
    public async Task Join_AFetchFailureOfKindOther_OnACorruptClone_SidelinesIt()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        var clone = Clone(rig, remote.Url);
        File.WriteAllText(Path.Combine(clone, ".git", "shallow"), "garbage\n");

        var result = await rig.Service().JoinAsync(remote.Url, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(Siblings(clone, "repo.broken-*"));
        Assert.Contains(result.Lines, l => l.StartsWith("note: a damaged clone was kept at", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, l => l.StartsWith("note: a clone was kept at", StringComparison.Ordinal));
        Assert.True(await IsJoinedAsync(rig, remote.Url));
    }

    [GitFact]
    public async Task Join_ADamagedReusedClone_IsSidelinedAsBroken_AndClonedAfresh()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        var damages = new Dictionary<string, Action<SetupRig, string>>
        {
            ["git directory removed"] = (_, clone) => GitTestEnvironment.ForceDelete(Path.Combine(clone, ".git")),
            ["not a repository"] = (rig, _) => rig.Fault = c => c.IsFetch ? FaultingGitProcess.Failure(SyncGit.FailureKind.NotARepository) : null,
            ["origin cannot be set"] = (rig, _) => rig.Fault = c => c.IsAddOrigin ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, exit: 128) : null,
        };
        foreach (var (name, damage) in damages)
        {
            using var rig = new SetupRig();
            Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
            var clone = Clone(rig, remote.Url);
            File.WriteAllText(Path.Combine(clone, "marker.txt"), name);
            damage(rig, clone);

            var result = await rig.Service().JoinAsync(remote.Url, null);

            Assert.True(result.ExitCode == 0, $"{name}: {result.RefusalText}");
            var sidelined = Siblings(clone, "repo.broken-*").Single();
            Assert.Equal(name, File.ReadAllText(Path.Combine(sidelined, "marker.txt")));
            Assert.Empty(Siblings(clone, "repo.left-*"));
            Assert.True(await IsJoinedAsync(rig, remote.Url), name);
        }
    }

    [GitFact]
    public async Task Join_AFailingResetOfAReusedClone_SidelinesItOnce_AndClonesAfresh()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        var clone = Clone(rig, remote.Url);
        var failures = 0;
        rig.Fault = c =>
        {
            if (c.IsResetHard && failures++ == 0)
                return FaultingGitProcess.Failure(SyncGit.FailureKind.Other);
            return null;
        };

        var result = await rig.Service().JoinAsync(remote.Url, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(Siblings(clone, "repo.broken-*"));
        Assert.Empty(Siblings(clone, "repo.left-*"));
    }

    // ---- the record, after a fault ------------------------------------------------------------------------------

    [GitFact]
    public async Task Join_FaultsBetweenTheRecordSteps_AreRepaired()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        foreach (var point in new[] { "after-placement", "after-database" })
        {
            using var rig = new SetupRig();
            rig.RecordFault = name => name == point ? throw new InvalidOperationException("simulated crash") : Task.CompletedTask;
            var failed = await rig.Service().JoinAsync(remote.Url, null);
            Assert.Equal(SyncSetup.Refusal.Failed, failed.Refusal);
            Assert.Empty(rig.Config.Writes);

            rig.RecordFault = null;
            Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
            Assert.True(await IsJoinedAsync(rig, remote.Url), point);
        }

        // and an init that crashed after its push is repaired by a join from the same machine
        foreach (var point in new[] { "after-placement", "after-database" })
        {
            using var fresh = BareRemote.Create(0);
            using var rig = new SetupRig();
            rig.RecordFault = name => name == point ? throw new InvalidOperationException("simulated crash") : Task.CompletedTask;
            Assert.Equal(SyncSetup.Refusal.Failed, (await rig.Service().InitAsync(fresh.Url, null)).Refusal);

            rig.RecordFault = null;
            Assert.Equal(0, (await rig.Service().JoinAsync(fresh.Url, null)).ExitCode);
            Assert.True(await IsJoinedAsync(rig, fresh.Url), "init then join after " + point);
        }
    }

    [GitFact]
    public async Task Join_APlacementThatCannotRename_IsANote_AndTheJoinStillSucceeds()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        rig.Move = (_, _) => throw new IOException("directory is locked");

        var result = await rig.Service().JoinAsync(remote.Path, null); // a respelled address

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.StartsWith("note:", StringComparison.Ordinal));
        Assert.Equal(remote.Path, Meta(rig, SyncIdentity.MetaKeys.Remote));
    }

    [GitFact]
    public async Task Join_ARenameThatFailsWhileSidelining_IsFailed_AndNothingIsRecorded()
    {
        var (remote, oldId) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        SeedBookkeeping(rig);
        remote.Recreate();
        using (var third = new SetupRig { MachineIdText = "machine-three", Seed = 7 })
            Assert.Equal(0, (await third.Service().InitAsync(remote.Url, null)).ExitCode);
        rig.Move = (_, _) => throw new IOException("directory is locked");

        var result = await rig.Service().JoinAsync(remote.Url, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("directory is locked", result.RefusalText);
        Assert.Equal(oldId, Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Equal(1, Count(rig, "git_sync_entry"));
    }

    [GitFact]
    public async Task Join_RendersTheConfigAtWriteTime_SoAConcurrentConfigSetIsKept()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        rig.RecordFault = name =>
        {
            if (name == "after-database")
                File.AppendAllText(rig.ConfigPath, "\n[extra]\nkey = 1\n");
            return Task.CompletedTask;
        };

        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);

        Assert.Contains("[extra]", Assert.Single(rig.Config.Writes));
    }

    [GitFact]
    public async Task Join_LeavesAnOrphanContentRowInPlace()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Exec(rig, "INSERT INTO hot_memories (id, content, created_at, updated_at, last_accessed_at) VALUES ('orphan','kept',1,1,1)");

        Assert.Equal(0, (await rig.Service().JoinAsync(remote.Url, null)).ExitCode);
        Assert.Equal(1, Count(rig, "hot_memories"));
        using var empty = BareRemote.Create(0);
        Assert.Equal(SyncSetup.Refusal.NothingToJoin, (await rig.Service().JoinAsync(empty.Url, null)).Refusal);
        Assert.Equal(1, Count(rig, "hot_memories"));
    }

    [GitFact]
    public async Task Join_NeverPrintsAStoredCredential()
    {
        var (remote, _) = await NewSyncRepository();
        using var _ = remote;
        using var rig = new SetupRig();
        Exec(rig, $"INSERT INTO _meta (key, value) VALUES ('{SyncIdentity.MetaKeys.Remote}', 'https://tok:secretXYZ@old.example/o/r')");
        Exec(rig, $"INSERT INTO _meta (key, value) VALUES ('{SyncIdentity.MetaKeys.RepoId}', 'old')");
        var oldClone = Clone(rig, "https://tok:secretXYZ@old.example/o/r");
        Directory.CreateDirectory(oldClone);

        var result = await rig.Service().JoinAsync(remote.Url, null);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("secretXYZ", string.Join("\n", result.Lines.Concat(result.Notes).Concat(rig.Reported)));
    }
}
