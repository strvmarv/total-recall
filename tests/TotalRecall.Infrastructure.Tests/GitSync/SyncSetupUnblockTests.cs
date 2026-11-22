using System.Globalization;
using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Git sync secret block (#161, spec D7): <c>sync unblock &lt;id&gt;</c> allows the current fields of a memory the scan held
/// back. Under the sync lock and in one immediate transaction it checks the entry, then the row, then the key, and writes
/// <c>allowed_hash</c> = the key with the mark cleared; it prints the categories found, never a value. Every token and key
/// comes from <see cref="FakeSecrets"/>.
/// </summary>
public sealed class SyncSetupUnblockTests : IDisposable
{
    private const string A = "3f9c2a7e";
    private const string B = "aaaa1111";
    private const long T1 = 1_800_000_000_000L;
    private const long T2 = T1 + 60_000;
    private const long T3 = T2 + 60_000;

    private static readonly FakeSecrets Fakes = new(59);

    private readonly SetupRig _rig = new();
    private MsSqliteConnection? _conn;
    private SqliteStore? _store;

    public void Dispose()
    {
        _conn?.Dispose();
        _rig.Dispose();
    }

    /// <summary>The test's own connection to the rig's database, migrated and joined on first use.</summary>
    private MsSqliteConnection Conn
    {
        get
        {
            if (_conn is null)
            {
                _conn = SyncDb.OpenMigrated(_rig.DbPath);
                _store = new SqliteStore(_conn);
                _store.SetMeta("git_sync_machine", ScanRig.MachineId);
            }
            return _conn;
        }
    }

    private SqliteStore Store
    {
        get
        {
            _ = Conn;
            return _store!;
        }
    }

    private void AddMemory(Tier tier, string id, string content, IReadOnlyList<string>? tags = null) =>
        Store.Insert(tier, ContentType.Memory, new InsertEntryOpts(Content: content, Tags: tags, Id: id, CreatedAt: 1_700_000_000_000L));

    private ScanSummary Scan(long now) =>
        Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(Conn, () => now).Scan()).Summary;

    private GitSyncRow? Row(string id) => GitSyncRows.Read(Conn, id);

    private void WriteRow(GitSyncRow row) => GitSyncRows.Write(Conn, row);

    /// <summary>The secret key of the entry as the store holds it now.</summary>
    private string KeyOf(string id)
    {
        var read = Assert.IsType<LocalRead.Present>(LocalEntryReader.Read(
            t => Store.Get(t, ContentType.Memory, id), () => Store.IsSticky(ContentType.Memory, id)));
        return SyncSecret.secretKey(read.View.Content, read.View.Tags);
    }

    /// <summary>The content fields of the entry as the store holds it now.</summary>
    private SyncMerge.ContentFields ContentOf(string id) =>
        Assert.IsType<LocalRead.Present>(LocalEntryReader.Read(
            t => Store.Get(t, ContentType.Memory, id), () => Store.IsSticky(ContentType.Memory, id))).View.Content;

    /// <summary>Every column of the row as stored, for a byte-for-byte comparison (or <c>none</c>).</summary>
    private string RowText(string id)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM git_sync_entry WHERE entry_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return "none";
        var sb = new StringBuilder();
        for (var i = 0; i < r.FieldCount; i++)
            sb.Append(r.GetName(i)).Append('=')
                .Append(r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>A memory with a token, held back by a scan.</summary>
    private string Blocked(string id, string token, IReadOnlyList<string>? tags = null)
    {
        AddMemory(Tier.Warm, id, "the token is " + token, tags);
        Scan(T1);
        Assert.Equal(SyncSecret.blockReason, Row(id)!.BlockedReason);
        return token;
    }

    /// <summary>
    /// The service over the rig. With <paramref name="cycleRan"/> (the default) the sync directory exists, as it does once a
    /// cycle has run; the command refuses <c>not-blocked</c> without it, before it creates anything.
    /// </summary>
    private SyncSetupService Service(Action<SyncSetupService>? configure = null, bool cycleRan = true)
    {
        if (cycleRan)
            GitSyncPaths.ForDatabase(_rig.DbPath, _rig.CaseInsensitive).EnsureCreated();
        var service = _rig.Service();
        configure?.Invoke(service);
        return service;
    }

    /// <summary>No piece of the token (any eight characters of it) is anywhere in what the command prints.</summary>
    private static void AssertNoToken(SetupResult result, string token)
    {
        var all = string.Join("\n", result.Lines.Concat(result.Notes).Append(result.RefusalText ?? ""));
        for (var i = 0; i + 8 <= token.Length; i++)
            Assert.DoesNotContain(token.Substring(i, 8), all, StringComparison.Ordinal);
    }

    private static void AssertRefused(SetupResult result, SyncSetup.Refusal refusal)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(refusal, result.Refusal);
        Assert.Empty(result.Lines);
    }

    // ---- the release ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("github-token", "API token or secret")]
    [InlineData("private-key", "private key block")]
    public async Task ABlockedMemory_IsReleased_AndTheCategoriesArePrinted(string secret, string category)
    {
        var token = Blocked(A, Fakes.Get(secret));
        AddMemory(Tier.Warm, B, "Use bun, not npm.");
        Scan(T2);
        var before = Row(A)!;
        var other = RowText(B);
        var key = KeyOf(A);

        var result = await Service().UnblockAsync(A);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Refusal);
        Assert.Equal(["unblocked " + A, "found: " + category], result.Lines);
        Assert.Equal(before with { BlockedReason = null, BlockedHash = null, AllowedHash = key }, Row(A));
        Assert.Equal(other, RowText(B));
        AssertNoToken(result, token);
    }

    [Fact]
    public async Task TheDetectorRunsOnlyAfterTheCommit_OnTheContentThatWasRead()
    {
        var token = Blocked(A, Fakes.Get("openai-key"), tags: ["clean", "also-clean"]);
        var seen = new List<string?>();
        string? allowedWhenDetecting = null;

        var result = await Service(s => s.SecretDetector = text =>
        {
            seen.Add(text);
            using var other = SyncDb.OpenMigrated(_rig.DbPath);
            allowedWhenDetecting ??= GitSyncRows.Read(other, A)!.AllowedHash;
            return SensitiveContentScanner.ScanSecrets(text);
        }).UnblockAsync(A);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(KeyOf(A), allowedWhenDetecting);
        Assert.Contains("the token is " + token, seen);
        Assert.Contains("clean", seen);
        Assert.Contains("also-clean", seen);
        Assert.Equal(["unblocked " + A, "found: API token or secret"], result.Lines);
        AssertNoToken(result, token);
    }

    [Fact]
    public async Task AContentTheDetectorNoLongerFlags_IsReleased_WithALineSayingSo()
    {
        Blocked(A, Fakes.Get("aws-key"));

        var result = await Service(s => s.SecretDetector = _ => []).UnblockAsync(A);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["unblocked " + A, "no secret was found in the current content"], result.Lines);
        Assert.Equal(KeyOf(A), Row(A)!.AllowedHash);
    }

    [Fact]
    public async Task ADetectorThatThrowsAfterTheCommit_StillReleases_WithALineSayingSo()
    {
        var token = Blocked(A, Fakes.Get("github-token"));
        var key = KeyOf(A);
        var before = Row(A)!;

        var result = await Service(s => s.SecretDetector = text => throw new InvalidOperationException(text)).UnblockAsync(A);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Refusal);
        Assert.Equal(["unblocked " + A, "the categories could not be checked"], result.Lines);
        Assert.Equal(before with { BlockedReason = null, BlockedHash = null, AllowedHash = key }, Row(A));
        AssertNoToken(result, token);
    }

    [Fact]
    public async Task ATagEditAfterTheRelease_IsBlockedAgainByTheNextScan()
    {
        Blocked(A, Fakes.Get("github-token"), tags: ["one"]);
        Assert.Equal(0, (await Service().UnblockAsync(A)).ExitCode);
        Scan(T2);
        Assert.Null(Row(A)!.BlockedReason);

        Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Tags = ["one", "two"] });
        var summary = Scan(T3);

        var row = Row(A)!;
        Assert.Equal(SyncSecret.blockReason, row.BlockedReason);
        Assert.Equal(KeyOf(A), row.BlockedHash);
        Assert.NotEqual(row.AllowedHash, row.BlockedHash);
        Assert.Equal([A], summary.Blocked);
    }

    [GitFact]
    public async Task AReleasedMemory_IsWrittenByTheNextPush()
    {
        using var push = new PushRig();
        push.Seed();
        var token = Fakes.Get("openai-key");
        push.AddMemory(Tier.Warm, A, content: "the key is " + token);
        push.Now += 1000;
        await push.PullAsync();
        Assert.Equal(SyncSecret.blockReason, GitSyncRows.Read(push.Pull.Connection, A)!.BlockedReason);

        var result = await new SyncSetupService(_rig.Env() with { DatabasePath = push.Pull.DbPath }).UnblockAsync(A);

        Assert.Equal(0, result.ExitCode);
        AssertNoToken(result, token);
        push.Now += 1000;
        var (apply, scan) = await push.PullAsync();
        var summary = Assert.IsType<PushOutcome.Pushed>(await push.Push(apply, scan)).Summary;
        Assert.Equal([A], summary.Written);
        Assert.Empty(summary.Blocked);
        Assert.Contains(
            SyncValidation.recordPath(A),
            push.Remote.Git("ls-tree", "-r", "--name-only", "main").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---- the entry ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnIdWithNoMemory_IsUnknownEntry()
    {
        var token = Blocked(A, Fakes.Get("github-token"));
        Store.Insert(Tier.Warm, ContentType.Knowledge, new InsertEntryOpts(Content: "A knowledge chunk.", Id: B));

        foreach (var id in new[] { "nope1234", B })
        {
            var result = await Service().UnblockAsync(id);
            AssertRefused(result, SyncSetup.Refusal.UnknownEntry);
            AssertNoToken(result, token);
        }
    }

    [Fact]
    public async Task AForgottenMemory_KeepingItsMark_IsUnknownEntry_AndNothingIsWritten()
    {
        var token = Blocked(A, Fakes.Get("github-token"));
        Store.Delete(Tier.Warm, ContentType.Memory, A);
        WriteRow(Row(A)! with { State = SyncMerge.RowState.Forgotten });
        var before = RowText(A);

        var result = await Service().UnblockAsync(A);

        AssertRefused(result, SyncSetup.Refusal.UnknownEntry);
        Assert.Equal(before, RowText(A));
        AssertNoToken(result, token);
    }

    [Fact]
    public async Task AMemoryRetypedToKnowledge_IsUnknownEntry_AndNothingIsWritten()
    {
        var token = Blocked(A, Fakes.Get("github-token"));
        Store.Move(Tier.Warm, ContentType.Memory, Tier.Warm, ContentType.Knowledge, A);
        Scan(T2);
        Assert.Equal(SyncMerge.RowState.Left, Row(A)!.State);
        var before = RowText(A);

        var result = await Service().UnblockAsync(A);

        AssertRefused(result, SyncSetup.Refusal.UnknownEntry);
        Assert.Equal(before, RowText(A));
        AssertNoToken(result, token);
    }

    [Fact]
    public async Task AnIdInTwoTiers_IsUnreadableEntry_EvenWithABlockedRow()
    {
        var token = Blocked(A, Fakes.Get("github-token"));
        AddMemory(Tier.Cold, A, "the same id, again");
        var before = RowText(A);

        var result = await Service().UnblockAsync(A);

        AssertRefused(result, SyncSetup.Refusal.UnreadableEntry);
        Assert.Equal(before, RowText(A));
        AssertNoToken(result, token);
    }

    [Fact]
    public async Task AMemoryThatCannotBeRead_IsUnreadableEntry_EvenWithABlockedRow()
    {
        var token = Blocked(A, Fakes.Get("github-token"));
        SyncDb.Exec(Conn, "UPDATE warm_memories SET source_tool = 'no-such-tool' WHERE id = $id", ("$id", A));
        var before = RowText(A);

        var result = await Service().UnblockAsync(A);

        AssertRefused(result, SyncSetup.Refusal.UnreadableEntry);
        Assert.Equal(before, RowText(A));
        AssertNoToken(result, token);
    }

    // ---- the row --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("no-row")]
    [InlineData("forgotten")]
    [InlineData("left")]
    [InlineData("too-large")]
    [InlineData("secret")]
    public async Task AMemoryWithoutASecretMark_IsNotBlocked_AndNothingIsWritten(string shape)
    {
        var token = Fakes.Get("github-token");
        if (shape == "no-row")
        {
            AddMemory(Tier.Warm, A, "the token is " + token);
        }
        else
        {
            Blocked(A, token);
            // a real writer mark stores SyncScan.contentHash, never the secret key, so the key check could not accept these
            // rows: the refusal can only come from the row check, which runs before it
            var row = Row(A)! with { BlockedHash = SyncScan.contentHash(ContentOf(A)) };
            Assert.NotEqual(KeyOf(A), row.BlockedHash);
            WriteRow(shape switch
            {
                "forgotten" => row with { State = SyncMerge.RowState.Forgotten },
                "left" => row with { State = SyncMerge.RowState.Left },
                _ => row with { BlockedReason = shape },
            });
        }
        var before = RowText(A);

        var result = await Service().UnblockAsync(A);

        AssertRefused(result, SyncSetup.Refusal.NotBlocked);
        Assert.Contains("not blocked by a secret", result.RefusalText);
        Assert.Equal(before, RowText(A));
        AssertNoToken(result, token);
    }

    // ---- the key --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEditAfterTheBlock_IsChangedSinceBlocked_AndNothingIsWritten()
    {
        var token = Blocked(A, Fakes.Get("github-token"));
        Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "now: " + token });
        var before = RowText(A);
        var detected = 0;

        var result = await Service(s => s.SecretDetector = t => { detected++; return []; }).UnblockAsync(A);

        AssertRefused(result, SyncSetup.Refusal.ChangedSinceBlocked);
        Assert.Contains("changed since it was blocked", result.RefusalText);
        Assert.Equal(before, RowText(A));
        Assert.Equal(0, detected);
        AssertNoToken(result, token);
    }

    // ---- busy, settings, the database ---------------------------------------------------------------------------

    [Fact]
    public async Task WhileTheSyncLockIsHeld_IsBusy_AndWritesNothing()
    {
        var token = Blocked(A, Fakes.Get("github-token"));
        var before = RowText(A);
        var paths = GitSyncPaths.ForDatabase(_rig.DbPath, false);
        paths.EnsureCreated();
        using (var held = await SyncLock.TryAcquireAsync(paths.LockPath, TimeSpan.Zero))
        {
            Assert.NotNull(held);
            var result = await Service().UnblockAsync(A);
            AssertRefused(result, SyncSetup.Refusal.Busy);
            AssertNoToken(result, token);
        }
        Assert.Equal(before, RowText(A));

        Assert.Equal(0, (await Service().UnblockAsync(A)).ExitCode);
    }

    [Fact]
    public async Task ABusyDatabase_IsBusy_AndWritesNothing()
    {
        var token = Blocked(A, Fakes.Get("github-token"));
        var before = RowText(A);
        using (var other = SyncDb.OpenMigrated(_rig.DbPath))
        {
            SyncDb.Exec(other, "BEGIN IMMEDIATE");
            var opened = 0;

            var result = await Service(s => s.AfterOpen = conn =>
            {
                opened++;
                // fail at once instead of waiting out SQLite's busy timeout and the 30 s command timeout
                SyncDb.Exec(conn, "PRAGMA busy_timeout = 0");
                conn.DefaultTimeout = 1;
            }).UnblockAsync(A);

            Assert.Equal(1, opened);
            AssertRefused(result, SyncSetup.Refusal.Busy);
            AssertNoToken(result, token);
            SyncDb.Exec(other, "ROLLBACK");
        }
        Assert.Equal(before, RowText(A));
    }

    [Fact]
    public async Task ALoadError_IsFailedAtOnce_AndCreatesNothing()
    {
        _rig.LoadError = "config.toml is malformed";

        var result = await Service(cycleRan: false).UnblockAsync(A);

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Equal("config.toml is malformed", result.RefusalText);
        _rig.AssertNothingCreated();
    }

    [Fact]
    public async Task AMissingDatabase_IsUnknownEntry_AndCreatesNothing()
    {
        var result = await Service(cycleRan: false).UnblockAsync(A);
        AssertRefused(result, SyncSetup.Refusal.UnknownEntry);
        _rig.AssertNothingCreated();

        var dataDir = Path.GetDirectoryName(_rig.DbPath)!;
        Directory.Delete(dataDir);
        var again = await Service(cycleRan: false).UnblockAsync(A);
        AssertRefused(again, SyncSetup.Refusal.UnknownEntry);
        Assert.False(Directory.Exists(dataDir), "the data directory was created");
    }

    [Fact]
    public async Task ADatabaseWithNoSyncDirectory_IsNotBlocked_AndCreatesNothing()
    {
        // no cycle ever ran, so no row can be secret-detected: the command must not create the sync directory
        AddMemory(Tier.Warm, A, "Use bun, not npm.");
        var dataDir = Path.GetDirectoryName(_rig.DbPath)!;
        var before = SnapshotOf(dataDir);

        var result = await Service(cycleRan: false).UnblockAsync(A);

        AssertRefused(result, SyncSetup.Refusal.NotBlocked);
        Assert.Equal("no sync has run on this database (there is no sync directory), so nothing is blocked; nothing changed", result.RefusalText);
        Assert.False(Directory.Exists(Path.Combine(dataDir, "sync")), "the sync directory was created");
        Assert.Equal(before, SnapshotOf(dataDir));
    }

    /// <summary>Every directory under the path, and every file other than the database's own, as sorted relative paths.</summary>
    private static string[] SnapshotOf(string dir) =>
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir, p))
            .Where(p => !p.StartsWith("total-recall.db", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task ADatabaseAtAnOlderSchema_IsMigrated_ThenChecked()
    {
        AddMemory(Tier.Warm, A, "Use bun, not npm.");
        foreach (var t in new[] { "git_sync_entry", "git_sync_review", "git_sync_issue", "git_sync_alias" })
            SyncDb.Exec(Conn, $"DROP TABLE {t}");
        SyncDb.Exec(Conn, "DROP INDEX idx_import_log_target_entry_id");
        SyncDb.Exec(Conn, "DELETE FROM _schema_version WHERE version = 18");

        var result = await Service().UnblockAsync(A);

        AssertRefused(result, SyncSetup.Refusal.NotBlocked);
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM _schema_version";
        Assert.Equal(18L, (long)cmd.ExecuteScalar()!);
        Assert.Equal("none", RowText(A));
    }
}
