using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class SyncSetupLeaveStatusTests
{
    private const string Remote = "git@host:o/r.git";

    private static readonly string[] LockFiles = ["lock", "no-hooks", "empty"];

    /// <summary>The files and directories under the root with their hashes, minus the three the lock creates.</summary>
    private static Dictionary<string, string> Listing(string root) =>
        SyncDb.Snapshot(root).Where(kv => !LockFiles.Contains(Path.GetFileName(kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static string Line(SetupResult result, string label) =>
        result.Lines.Single(l => l.StartsWith(label + ":", StringComparison.Ordinal));

    // ---- leave --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Leave_InGitMode_WritesOnlyTheLocalMode_AndChangesNothingElse()
    {
        using var rig = new SetupRig { Mode = "git", Settings = GuardSettings.For(Remote, "main") };
        File.WriteAllText(rig.ConfigPath, "[tiers.hot]\nmax_entries = 7\n\n[git_sync]\nremote = \"" + Remote + "\"\n");
        SyncDb.CreateMigrated(rig.DbPath);
        var clone = Path.Combine(rig.PathsFor(Remote).CloneDirectory, ".git");
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(clone, "HEAD"), "ref: refs/heads/main\n");
        var before = Listing(rig.Root);
        var configBefore = File.ReadAllText(rig.ConfigPath);

        var result = await rig.Service().LeaveAsync();

        Assert.Equal(0, result.ExitCode);
        var write = Assert.Single(rig.Config.Writes);
        var model = Tomlyn.Toml.ToModel(write);
        Assert.Equal("local", ((Tomlyn.Model.TomlTable)model["storage"])["mode"]);
        Assert.Equal(7L, ((Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)model["tiers"])["hot"])["max_entries"]);
        Assert.Equal(Remote, ((Tomlyn.Model.TomlTable)model["git_sync"])["remote"]);
        Assert.Single(rig.Config.Renders); // only the local-mode key
        Assert.Equal(("storage.mode", (object)"local"), rig.Config.Renders[0].Single());
        Assert.NotEqual(configBefore, File.ReadAllText(rig.ConfigPath));

        var after = Listing(rig.Root);
        after.Remove(Path.GetRelativePath(rig.Root, rig.ConfigPath));
        before.Remove(Path.GetRelativePath(rig.Root, rig.ConfigPath));
        Assert.Equal(before, after);
        Assert.Contains("untouched", string.Join(" ", result.Lines));
    }

    [Fact]
    public async Task Leave_NotInGitMode_ChangesNothing_AndCreatesNothing()
    {
        using var rig = new SetupRig { Mode = "local" };
        var result = await rig.Service().LeaveAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("this machine is not in git mode; nothing changed", result.Lines);
        Assert.Empty(rig.Config.Writes);
        rig.AssertNothingCreated();
    }

    [Fact]
    public async Task Leave_WorksWithoutGit_AndWithoutAConfiguredRemote()
    {
        using var rig = new SetupRig { Mode = "git", Settings = GuardSettings.For(null) };
        var empty = Path.Combine(rig.Root, "empty-path");
        Directory.CreateDirectory(empty);
        rig.PathVariable = empty;

        var result = await rig.Service().LeaveAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Single(rig.Config.Writes);
        Assert.Empty(rig.Recorder.Calls);
    }

    [Fact]
    public async Task Leave_WhileTheLockIsHeld_IsBusy_AndTheLockIsFreeAfterwards()
    {
        using var rig = new SetupRig { Mode = "git" };
        var paths = GitSyncPaths.ForDatabase(rig.DbPath, false);
        paths.EnsureCreated();
        using (var held = await SyncLock.TryAcquireAsync(paths.LockPath, TimeSpan.Zero))
        {
            Assert.NotNull(held);
            var busy = await rig.Service().LeaveAsync();
            Assert.Equal(SyncSetup.Refusal.Busy, busy.Refusal);
            Assert.Empty(rig.Config.Writes);
        }

        var result = await rig.Service().LeaveAsync();
        Assert.Equal(0, result.ExitCode);
        using var again = await SyncLock.TryAcquireAsync(paths.LockPath, TimeSpan.Zero);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Leave_AFailingConfigWrite_IsFailed()
    {
        using var rig = new SetupRig { Mode = "git" };
        rig.Config.WriteFault = _ => new IOException("disk full");
        var result = await rig.Service().LeaveAsync();
        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("disk full", result.RefusalText);
        Assert.Contains("writing config.toml", result.RefusalText);
    }

    [Fact]
    public async Task Leave_ALoadError_IsFailed()
    {
        using var rig = new SetupRig { Mode = "git", LoadError = "config.toml is malformed" };
        var result = await rig.Service().LeaveAsync();
        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Empty(rig.Config.Writes);
    }

    // ---- status -------------------------------------------------------------------------------------------------

    [GitFact]
    public async Task Status_BeforeAnySetup_PrintsTheFacts_AndCreatesNothing()
    {
        using var rig = new SetupRig();
        Directory.Delete(Path.GetDirectoryName(rig.DbPath)!);
        var result = await rig.Service().StatusAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(Path.GetDirectoryName(rig.DbPath)!));
        Assert.Matches(@"^git: \d+\.\d+$", Line(result, "git"));
        Assert.Equal("storage mode: local", Line(result, "storage mode"));
        Assert.Equal("configured remote: not set", Line(result, "configured remote"));
        Assert.Equal("recorded remote: not recorded", Line(result, "recorded remote"));
        Assert.Equal("repository id: not recorded", Line(result, "repository id"));
        Assert.Equal("machine id: not recorded", Line(result, "machine id"));
        Assert.Equal("host check: not recorded", Line(result, "host check"));
        Assert.Equal("clone: no clone", Line(result, "clone"));
        Assert.Contains("git sync has no [git_sync] remote configured", Line(result, "join state"));
        Assert.Equal("last sync: never (this build does not run the sync cycle)", result.Lines[^1]);
    }

    private static void SeedIdentity(SetupRig rig, string? remote, string? repoId, string? host, string? machine)
    {
        using var conn = SyncDb.OpenMigrated(rig.DbPath);
        foreach (var (key, value) in new[]
                 {
                     (SyncIdentity.MetaKeys.Remote, remote), (SyncIdentity.MetaKeys.RepoId, repoId),
                     (SyncIdentity.MetaKeys.Host, host), (SyncIdentity.MetaKeys.Machine, machine),
                 })
        {
            if (value is not null)
                SyncDb.Exec(conn, "INSERT INTO _meta (key, value) VALUES ($k, $v)", ("$k", key), ("$v", value));
        }
        conn.Dispose();
        MsSqliteConnection.ClearAllPools();
    }

    [GitFact]
    public async Task Status_ReportsEachJoinStateWithoutAClone()
    {
        // not configured
        using (var rig = new SetupRig())
        {
            SeedIdentity(rig, Remote, "aaaa", rig.Host.Fingerprint, "01234567");
            var r = await rig.Service().StatusAsync();
            Assert.Contains("not joined: git sync has no [git_sync] remote configured", Line(r, "join state"));
        }
        // never joined
        using (var rig = new SetupRig { Settings = GuardSettings.For(Remote) })
        {
            SyncDb.CreateMigrated(rig.DbPath);
            var r = await rig.Service().StatusAsync();
            Assert.Contains("has not joined a sync repository", Line(r, "join state"));
            Assert.Equal("configured remote: " + Remote, Line(r, "configured remote"));
            Assert.Equal("configured branch: main", Line(r, "configured branch"));
        }
        // remote changed
        using (var rig = new SetupRig { Settings = GuardSettings.For(Remote) })
        {
            SeedIdentity(rig, "git@other:o/r.git", "aaaa", rig.Host.Fingerprint, "01234567");
            var r = await rig.Service().StatusAsync();
            Assert.Contains("differs from the repository this machine joined", Line(r, "join state"));
            Assert.Equal("recorded remote: git@other:o/r.git", Line(r, "recorded remote"));
        }
        // data directory moved
        using (var rig = new SetupRig { Settings = GuardSettings.For(Remote) })
        {
            SeedIdentity(rig, Remote, "aaaa", "someotherhostfingerprint0000000000", "01234567");
            var r = await rig.Service().StatusAsync();
            Assert.Contains("moved or was copied", Line(r, "join state"));
            Assert.Equal("host check: differs", Line(r, "host check"));
        }
        // joined, host matches
        using (var rig = new SetupRig { Settings = GuardSettings.For(Remote) })
        {
            SeedIdentity(rig, Remote, "aaaa", rig.Host.Fingerprint, "01234567");
            var r = await rig.Service().StatusAsync();
            Assert.Equal("join state: joined", Line(r, "join state"));
            Assert.Equal("host check: matches", Line(r, "host check"));
            Assert.Equal("repository id: aaaa", Line(r, "repository id"));
            Assert.Equal("machine id: 01234567", Line(r, "machine id"));
            Assert.Equal("clone: no clone", Line(r, "clone"));
        }
        // the host cannot be identified
        using (var rig = new SetupRig { Settings = GuardSettings.For(Remote), MachineIdKind = MachineIdKind.Failed })
        {
            SeedIdentity(rig, Remote, "aaaa", "somehostfingerprint", "01234567");
            var r = await rig.Service().StatusAsync();
            Assert.Equal("host check: unavailable", Line(r, "host check"));
        }
    }

    private static void MakeClone(SetupRig rig, byte[]? manifest, bool helperFiles = true)
    {
        if (helperFiles)
            rig.PathsFor(Remote).EnsureCreated(); // a clone exists only after a sync command ran, which made these
        var dir = rig.PathsFor(Remote).CloneDirectory;
        Directory.CreateDirectory(dir);
        rig.GitEnv.RunGit(dir, "init", "-b", "main");
        if (manifest is not null)
            File.WriteAllBytes(Path.Combine(dir, "manifest.json"), manifest);
        else
            File.WriteAllText(Path.Combine(dir, "other.txt"), "x");
        rig.GitEnv.RunGit(dir, "add", "-A");
        rig.GitEnv.RunGit(dir, "-c", "user.name=t", "-c", "user.email=t@e", "commit", "-m", "x");
    }

    [GitFact]
    public async Task Status_ReadsTheManifestInTheClone_WithNoNetworkCommand()
    {
        var cases = new (byte[]? Bytes, string Expect)[]
        {
            (ManifestFile.Render(new string('a', 32), "total-recall 1.0", "2026-11-16T10:30:00Z"), "manifest ok"),
            (Encoding.UTF8.GetBytes("{not json"), "manifest invalid"),
            (Encoding.UTF8.GetBytes("{\"format\":\"total-recall/v2\"}"), "written by a newer total-recall"),
            (null, "the clone has no manifest"),
        };
        foreach (var (bytes, expect) in cases)
        {
            using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
            SeedIdentity(rig, Remote, new string('a', 32), rig.Host.Fingerprint, "01234567");
            MakeClone(rig, bytes);

            var before = SyncDb.Snapshot(rig.Root);
            var result = await rig.Service().StatusAsync();
            MsSqliteConnection.ClearAllPools();
            Assert.Equal(before, SyncDb.Snapshot(rig.Root));

            Assert.Contains(expect, Line(result, "clone"));
            Assert.StartsWith("clone: clone at ", Line(result, "clone"));
            Assert.All(rig.Recorder.Calls, c =>
                Assert.True(c.Command.IsVersion || c.Command.IsLsTreeRoot || c.Command.IsCatBlob, $"unexpected {c.Command}"));
        }
    }

    [GitFact]
    public async Task Status_ACloneWithoutTheHelperFiles_DoesNotReadTheManifest_AndCreatesNothing()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        SeedIdentity(rig, Remote, new string('a', 32), rig.Host.Fingerprint, "01234567");
        MakeClone(rig, ManifestFile.Render(new string('a', 32), "total-recall 1.0", "2026-11-16T10:30:00Z"), helperFiles: false);
        var before = SyncDb.Snapshot(rig.Root);

        var result = await rig.Service().StatusAsync();

        MsSqliteConnection.ClearAllPools();
        Assert.Equal(before, SyncDb.Snapshot(rig.Root));
        Assert.Contains("manifest not read", Line(result, "clone"));
        Assert.Equal("join state: joined", Line(result, "join state"));
    }

    [GitFact]
    public async Task Status_WithGitMissing_StillFindsTheClone()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        SeedIdentity(rig, Remote, new string('a', 32), rig.Host.Fingerprint, "01234567");
        MakeClone(rig, ManifestFile.Render(new string('a', 32), "total-recall 1.0", "2026-11-16T10:30:00Z"));
        var empty = Path.Combine(rig.Root, "empty-path");
        Directory.CreateDirectory(empty);
        rig.PathVariable = empty;

        var result = await rig.Service().StatusAsync();

        Assert.Equal("git: not found", Line(result, "git"));
        Assert.StartsWith("clone: clone at ", Line(result, "clone"));
    }

    [GitFact]
    public async Task Status_NeverPrintsACredential()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For("https://tokenAAA@host/x") };
        SeedIdentity(rig, "https://u:secretBBB@host/x", "aaaa", rig.Host.Fingerprint, "01234567");

        var result = await rig.Service().StatusAsync();

        var all = string.Join("\n", result.Lines);
        Assert.DoesNotContain("tokenAAA", all);
        Assert.DoesNotContain("secretBBB", all);
        Assert.Contains("https://***@host/x", all);
    }

    [GitFact]
    public async Task Status_OnTheRealTypeScriptShape_ReadsMetaAndExitsZero()
    {
        using var rig = new SetupRig();
        SyncDb.CreatePlain(
            rig.DbPath,
            "CREATE TABLE _schema_version (version INTEGER NOT NULL, applied_at INTEGER NOT NULL)",
            "INSERT INTO _schema_version VALUES (3, 0)",
            "CREATE TABLE _meta (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)",
            "INSERT INTO _meta VALUES ('git_sync_repo_id', 'legacy-id')",
            "CREATE TABLE hot_memories (id TEXT PRIMARY KEY NOT NULL, content TEXT NOT NULL)");
        var before = SyncDb.Snapshot(Path.GetDirectoryName(rig.DbPath)!);

        var result = await rig.Service().StatusAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("repository id: legacy-id", Line(result, "repository id"));
        MsSqliteConnection.ClearAllPools();
        Assert.Equal(before, SyncDb.Snapshot(Path.GetDirectoryName(rig.DbPath)!));
    }

    [GitFact]
    public async Task Status_AGarbageDatabase_PrintsUnknown_AndExitsZero()
    {
        using var rig = new SetupRig();
        File.WriteAllText(rig.DbPath, string.Concat(Enumerable.Repeat("not sqlite. ", 200)));
        var result = await rig.Service().StatusAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("repository id: unknown (", Line(result, "repository id"));
        Assert.StartsWith("recorded remote: unknown (", Line(result, "recorded remote"));
        Assert.StartsWith("host check: unknown (", Line(result, "host check"));
    }

    [GitFact]
    public async Task Status_AWalWithoutItsPartner_PrintsUnknown_AndCreatesNothing()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        SyncDb.CreateMigrated(rig.DbPath);
        File.WriteAllBytes(rig.DbPath + "-wal", []);
        var before = SyncDb.Snapshot(rig.Root);

        var result = await rig.Service().StatusAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("unknown (a write-ahead log is pending", Line(result, "repository id"));
        Assert.Contains("unknown (a write-ahead log is pending", Line(result, "join state"));
        Assert.Equal(before, SyncDb.Snapshot(rig.Root));
    }

    [GitFact]
    public async Task Status_ALoadError_PrintsUnknownForTheFactsThatNeedIt_AndStillReportsGit()
    {
        using var rig = new SetupRig { LoadError = "config.toml is malformed" };
        var result = await rig.Service().StatusAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("storage mode: unknown (config.toml is malformed)", Line(result, "storage mode"));
        Assert.Equal("configured remote: unknown (config.toml is malformed)", Line(result, "configured remote"));
        Assert.Equal("last sync: never (this build does not run the sync cycle)", result.Lines[^1]);
        Assert.Matches(@"^git: \d+\.\d+$", Line(result, "git"));
        Assert.StartsWith("host check: unknown (", Line(result, "host check"));
        rig.AssertNothingCreated();
    }

    // ---- status: the blocked list (#161, D8) ---------------------------------------------------------------------

    private const string LastSyncLine = "last sync: never (this build does not run the sync cycle)";

    /// <summary>A migrated database with these sync rows (an unknown state is written past the CHECK constraint).</summary>
    private static void SeedRows(SetupRig rig, params (string Id, string State, string? Reason)[] rows)
    {
        using var conn = SyncDb.OpenMigrated(rig.DbPath);
        SyncDb.Exec(conn, "PRAGMA ignore_check_constraints = 1");
        foreach (var (id, state, reason) in rows)
            SyncDb.Exec(
                conn,
                "INSERT INTO git_sync_entry (entry_id, state, blocked_reason, blocked_hash) VALUES ($id, $state, $reason, $hash)",
                ("$id", id), ("$state", state), ("$reason", (object?)reason ?? DBNull.Value),
                ("$hash", reason is null ? DBNull.Value : new string('a', 64)));
        SyncDb.Exec(conn, "PRAGMA ignore_check_constraints = 0");
        conn.Dispose();
        MsSqliteConnection.ClearAllPools();
    }

    /// <summary>The <c>blocked</c> section: its count line and the indented lines after it, which end right before the last line.</summary>
    private static string[] BlockedSection(SetupResult result)
    {
        var lines = result.Lines.ToArray();
        var start = Array.FindIndex(lines, l => l.StartsWith("blocked:", StringComparison.Ordinal));
        Assert.True(start >= 0, "no blocked line");
        Assert.Equal(LastSyncLine, lines[^1]);
        return lines[start..^1];
    }

    [GitFact]
    public async Task Status_ListsOnlyActiveRowsWithAReason_OrderedById()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        SeedRows(
            rig,
            ("3c000000", "active", "secret-detected"),
            ("1a000000", "active", "too-large"),
            ("2b000000", "active", null),
            ("0f000000", "forgotten", "secret-detected"),
            ("0e000000", "left", "secret-detected"),
            ("0d000000", "paused", "secret-detected"),
            ("2c000000", "active", "secret-detected"));

        var result = await rig.Service().StatusAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["blocked: 3", "  1a000000  too-large", "  2c000000  secret-detected", "  3c000000  secret-detected"],
            BlockedSection(result));
    }

    [GitFact]
    public async Task Status_With25Blocked_ShowsTheFirst20ById_AndHowManyMore()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        var ids = Enumerable.Range(0, 25).Select(i => $"3f9c2a{i:x2}").ToArray();
        SeedRows(rig, [.. ids.Reverse().Select(id => (id, "active", (string?)"secret-detected"))]);

        var section = BlockedSection(await rig.Service().StatusAsync());

        Assert.Equal(
            ["blocked: 25", .. ids[..20].Select(id => $"  {id}  secret-detected"), "  and 5 more"],
            section);
    }

    [GitFact]
    public async Task Status_NoBlockedRow_PrintsZero()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        SeedRows(rig, ("3f9c2a7e", "active", null));
        Assert.Equal(["blocked: 0"], BlockedSection(await rig.Service().StatusAsync()));
    }

    [GitFact]
    public async Task Status_AnUnmigratedDatabase_PrintsTheBlockedListAsUnknown_AndCreatesNothing()
    {
        using var rig = new SetupRig();
        SyncDb.CreatePlain(
            rig.DbPath,
            "CREATE TABLE _meta (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)",
            "CREATE TABLE hot_memories (id TEXT PRIMARY KEY NOT NULL, content TEXT NOT NULL)");
        var before = SyncDb.Snapshot(rig.Root);

        var result = await rig.Service().StatusAsync();

        MsSqliteConnection.ClearAllPools();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["blocked: unknown (the database has no git sync state: start total-recall once)"], BlockedSection(result));
        Assert.Equal(before, SyncDb.Snapshot(rig.Root));
    }

    [GitFact]
    public async Task Status_AMissingDatabaseFile_PrintsNoDatabase()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        Assert.False(File.Exists(rig.DbPath));

        var result = await rig.Service().StatusAsync();

        Assert.Equal(["blocked: unknown (no database)"], BlockedSection(result));
        rig.AssertNothingCreated();
    }

    [GitFact]
    public async Task Status_ALoadError_PrintsTheBlockedListAsUnknownWithTheReason()
    {
        using var rig = new SetupRig { LoadError = "config.toml is malformed" };
        SeedRows(rig, ("3f9c2a7e", "active", "secret-detected"));

        var result = await rig.Service().StatusAsync();

        Assert.Equal(["blocked: unknown (config.toml is malformed)"], BlockedSection(result));
    }

    [GitFact]
    public async Task Status_AnUnknownOpenState_PrintsTheBlockedListAsUnknownWithTheReason()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        SeedRows(rig, ("3f9c2a7e", "active", "secret-detected"));
        File.WriteAllBytes(rig.DbPath + "-wal", []);

        var result = await rig.Service().StatusAsync();

        Assert.Equal(["blocked: unknown (" + ReadOnlyDatabase.PendingWalText + ")"], BlockedSection(result));
    }

    [GitFact]
    public async Task Status_ListingBlockedRows_TakesNoLock_AndCreatesNothing()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        SeedRows(rig, ("3f9c2a7e", "active", "secret-detected"));
        string[] expected = ["blocked: 1", "  3f9c2a7e  secret-detected"];
        var before = SyncDb.Snapshot(rig.Root);

        var result = await rig.Service().StatusAsync();

        MsSqliteConnection.ClearAllPools();
        Assert.Equal(expected, BlockedSection(result));
        Assert.Equal(before, SyncDb.Snapshot(rig.Root));
        Assert.False(Directory.Exists(rig.SyncDirectory), "status created the sync directory (and so a lock file)");

        // another process holds the sync lock: status neither waits for it nor refuses
        var paths = GitSyncPaths.ForDatabase(rig.DbPath, false);
        paths.EnsureCreated();
        using var held = await SyncLock.TryAcquireAsync(paths.LockPath, TimeSpan.Zero);
        Assert.NotNull(held);
        var whileHeld = await rig.Service().StatusAsync();
        Assert.Equal(0, whileHeld.ExitCode);
        Assert.Equal(expected, BlockedSection(whileHeld));
    }

    // ---- status creates nothing -----------------------------------------------------------------------------

    private static async Task AssertStatusCreatesNothing(SetupRig rig, string watched)
    {
        var before = SyncDb.Snapshot(watched);
        var result = await rig.Service().StatusAsync();
        MsSqliteConnection.ClearAllPools();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(before, SyncDb.Snapshot(watched));
    }

    [GitFact]
    public async Task Status_OnACleanDatabase_CreatesNothing()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        SyncDb.CreateMigrated(rig.DbPath);
        Assert.False(File.Exists(rig.DbPath + "-wal"));
        await AssertStatusCreatesNothing(rig, Path.GetDirectoryName(rig.DbPath)!);
        Assert.False(File.Exists(rig.DbPath + "-wal"));
        Assert.False(File.Exists(rig.DbPath + "-shm"));
        Assert.False(Directory.Exists(rig.SyncDirectory), "status created the sync directory");
    }

    [NonRootShFact(RequiresGit = true)]
    public async Task Status_OnACleanDatabaseInAReadOnlyDirectory_CreatesNothing()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        SyncDb.CreateMigrated(rig.DbPath);
        var dir = Path.GetDirectoryName(rig.DbPath)!;
        Posix.Mode(dir, (UnixFileMode)0b101_101_101);
        try
        {
            await AssertStatusCreatesNothing(rig, dir);
        }
        finally
        {
            Posix.Mode(dir, (UnixFileMode)0b111_111_111);
        }
    }

    [GitShFact]
    public async Task Status_ThroughASymbolicLink_CreatesNothing()
    {
        using var rig = new SetupRig { Settings = GuardSettings.For(Remote) };
        var real = Path.Combine(rig.Root, "real", "total-recall.db");
        SyncDb.CreateMigrated(real);
        File.Delete(rig.DbPath);
        File.CreateSymbolicLink(rig.DbPath, real);
        await AssertStatusCreatesNothing(rig, rig.Root);
        Assert.False(File.Exists(real + "-wal"));
    }
}
