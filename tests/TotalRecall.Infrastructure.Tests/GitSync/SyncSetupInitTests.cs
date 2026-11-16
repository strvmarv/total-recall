using System.Text;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class SyncSetupInitTests
{
    private static string Refs(BareRemote remote) => remote.Git("for-each-ref", "--format=%(refname)").Trim();

    private static string? Meta(SetupRig rig, string key)
    {
        using var conn = SyncDb.OpenMigrated(rig.DbPath);
        return SyncDb.Meta(conn, key);
    }

    private static void SeedOldIdentity(SetupRig rig, string oldRemote)
    {
        using var conn = SyncDb.OpenMigrated(rig.DbPath);
        foreach (var (k, v) in new[]
                 {
                     (SyncIdentity.MetaKeys.Remote, oldRemote), (SyncIdentity.MetaKeys.RepoId, "oldrepo"), (SyncIdentity.MetaKeys.Host, "oldhost"),
                     (SyncIdentity.MetaKeys.Machine, "0badc0de"), (SyncIdentity.MetaKeys.LastSha, "abc"), (SyncIdentity.MetaKeys.Arrivals, "3"),
                     ("git_sync_hlc", "keep-me"),
                 })
            SyncDb.Exec(conn, "INSERT INTO _meta (key, value) VALUES ($k, $v)", ("$k", k), ("$v", v));
        SyncDb.Exec(conn, "INSERT INTO git_sync_entry (entry_id, state) VALUES ('e1','active')");
        SyncDb.Exec(conn, "INSERT INTO git_sync_issue (entry_id, kind) VALUES ('e1','apply-failed')");
        SyncDb.Exec(conn, "INSERT INTO git_sync_alias (canonical_id, other_id, state) VALUES ('a','b','open')");
        SyncDb.Exec(conn, "INSERT INTO git_sync_review (entry_id, kind, state, arrived_at) VALUES ('e1','new-pin','open',1), ('e1','conflict','open',1)");
        SyncDb.Exec(conn, "INSERT INTO hot_memories (id, content, created_at, updated_at, last_accessed_at) VALUES ('orphan','kept',1,1,1)");
    }

    // ---- the happy path ---------------------------------------------------------------------------------------

    [GitFact]
    public async Task Init_OnAnEmptyRepository_PushesExactlyOneCommit_AndRecordsEverything()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();

        var result = await rig.Service().InitAsync(remote.Url, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("refs/heads/main", Refs(remote));
        Assert.Equal(".gitattributes\nmanifest.json", remote.Git("ls-tree", "-r", "--name-only", "main").Trim().Replace("\r", ""));
        Assert.Equal("* -text\n", remote.Git("show", "main:.gitattributes"));
        Assert.Equal("1", remote.Git("rev-list", "--count", "main").Trim());
        var expectedId = SyncSetup.newRepoId(Enumerable.Range(0, 16).Select(i => (byte)(16 + i)).ToArray());
        var manifest = ManifestFile.Parse(Encoding.UTF8.GetBytes(remote.Git("show", "main:manifest.json")));
        Assert.Equal(ManifestParseKind.Ok, manifest.Kind);
        Assert.Equal(expectedId, manifest.Manifest!.RepoId);
        Assert.Equal("total-recall 9.9.9", manifest.Manifest.CreatedBy);
        Assert.Equal("2026-11-16T10:30:00Z", manifest.Manifest.CreatedAt);

        // the config: the dry run and the final render, both with exactly the three keys; one write
        Assert.Equal(2, rig.Config.Renders.Count);
        Assert.Equal(
            [("storage.mode", (object)"git"), ("git_sync.remote", remote.Url), ("git_sync.branch", "main")],
            rig.Config.Renders[^1]);
        Assert.Single(rig.Config.Writes);

        // the database
        Assert.Equal(remote.Url, Meta(rig, SyncIdentity.MetaKeys.Remote));
        Assert.Equal(expectedId, Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Equal(rig.Host.Fingerprint, Meta(rig, SyncIdentity.MetaKeys.Host));
        var machine = Meta(rig, SyncIdentity.MetaKeys.Machine);
        Assert.Matches("^[0-9a-f]{8}$", machine);
        Assert.True(FSharpOption<SyncStamp.MachineId>.get_IsSome(SyncStamp.MachineIdModule.tryCreate(machine!)));
        Assert.Contains(result.Lines, l => l.Contains("not part of this build yet"));
        Assert.DoesNotContain(result.Lines, l => l.Contains("tokenx"));

        // the 1a-2 guard now says joined
        var paths = rig.PathsFor(remote.Url);
        using var conn = SyncDb.OpenMigrated(rig.DbPath);
        var store = new SqliteStore(conn);
        var client = new GitClient(new GitProcess(), GitExecutable.TryFind()!, paths, new GitClientOptions { EnvironmentOverrides = rig.GitEnv.EnvironmentOverrides });
        var guard = new RepoGuard(GuardSettings.For(remote.Url, "main"), store, client, rig.Host, rig.Probe, () => rig.Now);
        var verdict = await guard.EvaluateIdentityAsync();
        Assert.True(verdict.Join.IsJoined);
        Assert.True(verdict.CanSync);
    }

    [GitShFact]
    public async Task Init_GivesTheSameCommitContentUnderAHostileGlobalConfiguration()
    {
        using var cleanRemote = BareRemote.Create(0);
        using var hostileRemote = BareRemote.Create(0);
        using var clean = new SetupRig();
        using var hostile = new SetupRig(hostile: true);

        Assert.Equal(0, (await clean.Service().InitAsync(cleanRemote.Url, null)).ExitCode);
        var result = await hostile.Service().InitAsync(hostileRemote.Url, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(cleanRemote.Git("rev-parse", "main^{tree}"), hostileRemote.Git("rev-parse", "main^{tree}"));
        Assert.Equal(cleanRemote.Git("show", "main:manifest.json"), hostileRemote.Git("show", "main:manifest.json"));
        Assert.Equal("* -text\n", hostileRemote.Git("show", "main:.gitattributes"));
        hostile.Markers!.AssertNoneFired();
    }

    [GitFact]
    public async Task Init_ReportsGitProgressWithATwoSpacePrefix()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();
        await rig.Service().InitAsync(remote.Url, null);
        Assert.Contains(rig.Reported, l => l.StartsWith("  ", StringComparison.Ordinal));
    }

    // ---- refusals at the look ---------------------------------------------------------------------------------

    [GitFact]
    public async Task Init_ARepositoryWithTheBranch_IsRepoExists_AndNothingChanges()
    {
        using var remote = BareRemote.Create();
        using var rig = new SetupRig();
        var before = Refs(remote);

        var result = await rig.Service().InitAsync(remote.Url, null);

        Assert.Equal(SyncSetup.Refusal.RepoExists, result.Refusal);
        Assert.Contains("sync join", result.RefusalText);
        Assert.Equal(before, Refs(remote));
        Assert.Null(Meta(rig, SyncIdentity.MetaKeys.Remote));
        Assert.Empty(rig.Config.Writes);
    }

    [GitFact]
    public async Task Init_ARepositoryWithOtherRefs_IsNotEmpty()
    {
        foreach (var refName in new[] { "refs/heads/dev", "refs/tags/v1", "refs/pull/1/head" })
        {
            using var remote = BareRemote.Create(0);
            remote.AddRef(refName, remote.AddEmptyCommit());
            using var rig = new SetupRig();
            var before = Refs(remote);

            var result = await rig.Service().InitAsync(remote.Url, null);

            Assert.Equal(SyncSetup.Refusal.NotEmpty, result.Refusal);
            Assert.Equal(before, Refs(remote));
            Assert.Null(Meta(rig, SyncIdentity.MetaKeys.RepoId));
            Assert.Empty(rig.Config.Writes);
        }
    }

    [GitFact]
    public async Task Init_ATooLongListing_IsNotEmpty_AndTheOtherFailuresAreClassified()
    {
        using var remote = BareRemote.Create(0);
        var cases = new (SyncGit.FailureKind Kind, SyncSetup.Refusal Expect)[]
        {
            (SyncGit.FailureKind.OutputTooLarge, SyncSetup.Refusal.NotEmpty),
            (SyncGit.FailureKind.Auth, SyncSetup.Refusal.NeedsAuth),
            (SyncGit.FailureKind.TimedOut, SyncSetup.Refusal.Unreachable),
            (SyncGit.FailureKind.Network, SyncSetup.Refusal.Unreachable),
            (SyncGit.FailureKind.RemoteNotFound, SyncSetup.Refusal.Unreachable),
            (SyncGit.FailureKind.Other, SyncSetup.Refusal.Failed),
        };
        foreach (var (kind, expect) in cases)
        {
            using var rig = new SetupRig();
            rig.Fault = c => c.IsLsRemoteRefs ? FaultingGitProcess.Failure(kind) : null;
            var result = await rig.Service().InitAsync(remote.Url, null);
            Assert.Equal(expect, result.Refusal);
            Assert.Empty(rig.Config.Writes);
            Assert.Null(Meta(rig, SyncIdentity.MetaKeys.RepoId));
        }
    }

    [GitFact]
    public async Task Init_ANonexistentRepository_IsUnreachable_WithTheNotFoundText()
    {
        using var rig = new SetupRig();
        var result = await rig.Service().InitAsync(new Uri(Path.Combine(rig.Root, "nowhere.git")).AbsoluteUri, null);
        Assert.Equal(SyncSetup.Refusal.Unreachable, result.Refusal);
        Assert.Contains("not found", result.RefusalText);
    }

    // ---- failures at the push ---------------------------------------------------------------------------------

    [GitShFact]
    public async Task Init_APushRejectedByTheRemote_RecordsNothing()
    {
        using var remote = BareRemote.Create(0);
        remote.InstallHook("pre-receive", "echo 'rejected by policy' >&2\nexit 1");
        using var rig = new SetupRig();

        var result = await rig.Service().InitAsync(remote.Url, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("rejected", result.RefusalText);
        Assert.DoesNotContain("failed to push some refs", result.RefusalText);
        Assert.Equal("", Refs(remote));
        Assert.Null(Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Empty(rig.Config.Writes);
        var clone = rig.PathsFor(remote.Url).CloneDirectory;
        Assert.True(Directory.Exists(Path.Combine(clone, ".git")), "the unpushed clone was removed");
        Assert.Contains(result.Notes, n => n.Contains("a clone was kept at"));

        // retry after the hook is gone: it succeeds and the first clone is sidelined, not deleted
        File.Delete(Path.Combine(remote.Path, "hooks", "pre-receive"));
        var retry = await rig.Service().InitAsync(remote.Url, null);
        Assert.Equal(0, retry.ExitCode);
        Assert.Contains(retry.Lines, l => l.StartsWith("note: the clone of an earlier init was kept at", StringComparison.Ordinal));
        Assert.DoesNotContain(retry.Lines, l => l.StartsWith("note: a clone was kept at", StringComparison.Ordinal));
        Assert.Equal("refs/heads/main", Refs(remote));
        var siblings = Directory.GetDirectories(Path.GetDirectoryName(clone)!);
        Assert.Contains(siblings, d => Path.GetFileName(d).StartsWith("repo.unpushed-20261116103000", StringComparison.Ordinal));
    }

    [GitFact]
    public async Task Init_ARaceAtThePush_IsRepoExists()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();
        rig.Before = c =>
        {
            if (c.IsPush)
                remote.PushCommit();
        };

        var result = await rig.Service().InitAsync(remote.Url, null);

        Assert.Equal(SyncSetup.Refusal.RepoExists, result.Refusal);
        Assert.Null(Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Empty(rig.Config.Writes);
    }

    [GitFact]
    public async Task Init_ATimedOutPush_SaysItMayHaveCompleted()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();
        rig.Fault = c => c.IsPush ? FaultingGitProcess.Failure(SyncGit.FailureKind.TimedOut) : null;
        var result = await rig.Service().InitAsync(remote.Url, null);
        Assert.Equal(SyncSetup.Refusal.Unreachable, result.Refusal);
        Assert.Contains("may have completed", result.RefusalText);
        Assert.Contains("sync join", result.RefusalText);
    }

    // ---- the record -------------------------------------------------------------------------------------------

    [GitFact]
    public async Task Init_AFailingConfigWrite_AfterThePush_SaysToRunJoin_AndTheIdentityIsRecorded()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();
        rig.Config.WriteFault = _ => new IOException("disk full");

        var result = await rig.Service().InitAsync(remote.Url, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("the repository was initialised but this machine could not record it", result.RefusalText);
        Assert.Contains("sync join", result.RefusalText);
        Assert.Contains("disk full", result.RefusalText);
        Assert.Equal("refs/heads/main", Refs(remote));
        Assert.NotNull(Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Empty(rig.Config.Writes);
    }

    [GitFact]
    public async Task Init_FaultsBetweenTheRecordSteps_AreRepaired()
    {
        foreach (var point in new[] { "after-placement", "after-database" })
        {
            using var remote = BareRemote.Create(0);
            using var rig = new SetupRig();
            rig.RecordFault = name => name == point ? throw new InvalidOperationException("simulated crash") : Task.CompletedTask;

            var result = await rig.Service().InitAsync(remote.Url, null);

            Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
            Assert.Contains("sync join", result.RefusalText);
            Assert.Contains(result.Notes, n => n.Contains("the repository was initialised"));
            Assert.Equal("refs/heads/main", Refs(remote));
            Assert.Empty(rig.Config.Writes);
            Assert.Equal(point == "after-database", Meta(rig, SyncIdentity.MetaKeys.RepoId) is not null);

            // a second init finds the repository and points to join (the repair is a join: see the join tests)
            rig.RecordFault = null;
            var again = await rig.Service().InitAsync(remote.Url, null);
            Assert.Equal(SyncSetup.Refusal.RepoExists, again.Refusal);
            Assert.Contains("sync join", again.RefusalText);
        }
    }

    [GitFact]
    public async Task Init_RendersTheConfigAtWriteTime_SoAConcurrentConfigSetIsKept()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();
        rig.RecordFault = name =>
        {
            if (name == "after-database")
                File.AppendAllText(rig.ConfigPath, "\n[extra]\nkey = 1\n");
            return Task.CompletedTask;
        };

        var result = await rig.Service().InitAsync(remote.Url, null);

        Assert.Equal(0, result.ExitCode);
        var write = Assert.Single(rig.Config.Writes);
        Assert.Contains("[extra]", write);
        Assert.Contains("remote = ", write);
    }

    [GitFact]
    public async Task Init_OnAMachineJoinedToAnotherRepository_ClearsItsBookkeeping_ButKeepsMemoriesAndConflicts()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();
        const string oldRemote = "https://oldtoken:secretXYZ@old.example/o/r";
        SeedOldIdentity(rig, oldRemote);
        var oldClone = rig.PathsFor(oldRemote).CloneDirectory;
        Directory.CreateDirectory(oldClone);
        File.WriteAllText(Path.Combine(oldClone, "marker.txt"), "precious");

        var result = await rig.Service().InitAsync(remote.Url, null);

        Assert.Equal(0, result.ExitCode);
        using (var conn = SyncDb.OpenMigrated(rig.DbPath))
        {
            Assert.Equal(0, SyncDb.Count(conn, "git_sync_entry"));
            Assert.Equal(0, SyncDb.Count(conn, "git_sync_issue"));
            Assert.Equal(0, SyncDb.Count(conn, "git_sync_alias"));
            Assert.Equal(1, SyncDb.Count(conn, "git_sync_review"));
            Assert.Equal(1, SyncDb.Count(conn, "hot_memories"));
            Assert.Null(SyncDb.Meta(conn, SyncIdentity.MetaKeys.LastSha));
            Assert.Null(SyncDb.Meta(conn, SyncIdentity.MetaKeys.Arrivals));
            Assert.Equal("keep-me", SyncDb.Meta(conn, "git_sync_hlc"));
            Assert.NotEqual("0badc0de", SyncDb.Meta(conn, SyncIdentity.MetaKeys.Machine));
            Assert.Equal(remote.Url, SyncDb.Meta(conn, SyncIdentity.MetaKeys.Remote));
        }
        Assert.False(Directory.Exists(oldClone));
        var sidelined = Directory.GetDirectories(Path.GetDirectoryName(oldClone)!, "repo.left-*").Single();
        Assert.Equal("precious", File.ReadAllText(Path.Combine(sidelined, "marker.txt")));
        // nothing printed holds the old credential
        var all = string.Join("\n", result.Lines.Concat(result.Notes));
        Assert.DoesNotContain("secretXYZ", all);
    }

    [GitFact]
    public async Task Init_LeavesAnOrphanContentRowInPlace()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();
        SeedOldIdentity(rig, "git@old:o/r.git");

        using var exists = BareRemote.Create();
        Assert.Equal(SyncSetup.Refusal.RepoExists, (await rig.Service().InitAsync(exists.Url, null)).Refusal);
        using (var conn = SyncDb.OpenMigrated(rig.DbPath))
            Assert.Equal(1, SyncDb.Count(conn, "hot_memories"));

        Assert.Equal(0, (await rig.Service().InitAsync(remote.Url, null)).ExitCode);
        using (var conn = SyncDb.OpenMigrated(rig.DbPath))
            Assert.Equal(1, SyncDb.Count(conn, "hot_memories"));
    }

    // ---- sidelining that cannot rename ------------------------------------------------------------------------

    [GitFact]
    public async Task Init_ARenameThatFailsBeforeTheRecord_IsFailed_AndNothingIsRecorded()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();
        var clone = rig.PathsFor(remote.Url).CloneDirectory;
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(clone, "x.txt"), "x");
        rig.Move = (_, _) => throw new IOException("directory is locked");

        var result = await rig.Service().InitAsync(remote.Url, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("directory is locked", result.RefusalText);
        Assert.Equal("", Refs(remote));
        Assert.Null(Meta(rig, SyncIdentity.MetaKeys.RepoId));
        Assert.Empty(rig.Config.Writes);
        Assert.True(File.Exists(Path.Combine(clone, "x.txt")));
    }

    [GitFact]
    public async Task Init_APlacementThatCannotRename_IsANote_AndTheInitStillSucceeds()
    {
        using var remote = BareRemote.Create(0);
        using var rig = new SetupRig();
        const string oldRemote = "git@old:o/r.git";
        SeedOldIdentity(rig, oldRemote);
        var oldClone = rig.PathsFor(oldRemote).CloneDirectory;
        Directory.CreateDirectory(oldClone);
        rig.Move = (_, _) => throw new IOException("directory is locked");

        var result = await rig.Service().InitAsync(remote.Url, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.StartsWith("note:", StringComparison.Ordinal) && l.Contains("could not be moved aside"));
        Assert.True(Directory.Exists(oldClone));
        Assert.Equal(remote.Url, Meta(rig, SyncIdentity.MetaKeys.Remote));
    }
}
