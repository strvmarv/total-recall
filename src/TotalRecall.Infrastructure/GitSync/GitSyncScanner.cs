using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using SyncRecordModule = TotalRecall.Core.SyncRecord;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// What one scan did (the counts are numbers of entries) and what it noticed. <paramref name="Blocked"/> and
/// <paramref name="Unblocked"/> are the ids (ordinal) whose secret block this scan set, and the ids whose secret block it
/// lifted (a clean re-scan, or fields the user allowed) (#161, decision D4).
/// </summary>
public sealed record ScanSummary(
    int New,
    int Content,
    int Tags,
    int Sticky,
    int Tier,
    int Left,
    int Reactivated,
    int Ineligible,
    IReadOnlyList<LocalDeletion> LocalDeletions,
    IReadOnlyList<SkippedItem> Skipped,
    IReadOnlyList<string> Blocked,
    IReadOnlyList<string> Unblocked)
{
    public static ScanSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, [], [], [], []);
}

/// <summary>An active row whose memory exists in no table. The scan reports it and changes nothing; a later milestone acts on it.</summary>
public sealed record LocalDeletion(string Id, bool HasBase, bool HasPushingStamps);

/// <summary>An entry or a group the scan did not process. <paramref name="Group"/> is <c>content</c>, <c>tags</c>, <c>sticky</c>, <c>tier</c>, or null for the whole entry.</summary>
public sealed record SkippedItem(string Id, string? Group, string Reason);

public abstract record ScanOutcome
{
    private ScanOutcome() { }

    public sealed record Done(ScanSummary Summary) : ScanOutcome;

    /// <summary>This machine has no valid sync machine id, so it has not joined a repository: nothing was done.</summary>
    public sealed record NotJoined : ScanOutcome;

    public sealed record Failed(string Reason) : ScanOutcome;
}

/// <summary>
/// The scan, the local step of the git sync cycle (milestone 1b-i-1): compares every eligible memory with its
/// <c>git_sync_entry</c> row and records what changed locally, stamping it with this machine's clock. It reads one
/// snapshot, decides in memory with <see cref="SyncScan"/>, and writes in one transaction. It takes no lock, runs no
/// <c>git</c>, uses no network, and never writes a memory, knowledge, vector, <c>import_log</c> or
/// <c>compaction_log</c> row: only <c>git_sync_entry</c>, <c>git_sync_issue</c> and the two clock keys of <c>_meta</c>, and
/// it removes the <c>held-delete</c> item of a row it moves to <c>left</c> or reactivates (milestone 1b-ii: the item
/// cannot outlive the base it was opened for; no other <c>git_sync_review</c> row is touched). Between the read and the
/// write it checks each entry for a token or a private key and sets or lifts the row's secret block (#161, decision D4).
/// </summary>
public sealed partial class GitSyncScanner
{
    private readonly MsSqliteConnection _conn;
    private readonly Func<long> _nowMs;

    /// <summary>Test seam (decision 13): called once, after the hot memory table was read and before the warm one.</summary>
    internal Action? AfterFirstTableRead { get; set; }

    /// <summary>
    /// Test seam (#161, decision D2): the secret detector the scan runs on each free-text field of an entry it checks.
    /// A non-empty result is a hit.
    /// </summary>
    internal Func<string?, IReadOnlyList<string>> SecretDetector { get; set; } = SensitiveContentScanner.ScanSecrets;

    public GitSyncScanner(MsSqliteConnection connection, Func<long> nowMs)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(nowMs);
        _conn = connection;
        _nowMs = nowMs;
    }

    public ScanOutcome Scan()
    {
        try
        {
            var snapshot = ReadSnapshot();
            if (!snapshot.Joined)
                return new ScanOutcome.NotJoined();
            var machine = SyncStamp.MachineIdModule.tryCreate(snapshot.MachineId!).Value;
            var plan = BuildPlan(snapshot, machine);
            if (plan.HasWrites)
                Write(plan);
            return new ScanOutcome.Done(plan.Summary);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ScanOutcome.Failed(ex.Message);
        }
    }

    /// <summary>How many write transactions this scanner opened (a scan with nothing to write opens none).</summary>
    internal int WriteTransactionCount { get; private set; }

    /// <summary>Called inside the open write transaction before each of the steps <c>rows</c>, <c>issues</c>, <c>clock</c> and <c>warning</c>.</summary>
    internal Action<string>? BeforeWrite { get; set; }

    // ------------------------------------------------------------------------------------------
    // The read side

    internal sealed record SnapshotEntry(SyncMerge.LocalEntry View, Tier Tier);

    internal sealed record IneligibleEntry(string Id, string TierName);

    internal sealed record Snapshot(
        string? MachineId,
        Dictionary<string, GitSyncRow> Rows,
        SortedDictionary<string, SnapshotEntry> Entries,
        HashSet<string> KnowledgeIds,
        HashSet<string> SkippedIds,
        Dictionary<string, string?> Issues,
        List<IneligibleEntry> Ineligible,
        List<SkippedItem> Skipped,
        string? ClockText,
        string? WarningText)
    {
        /// <summary>False when the machine id is missing or not valid: the rest of the snapshot is then empty.</summary>
        public bool Joined => MachineId is not null;
    }

    private static readonly Tier[] Tiers = [Tier.Hot, Tier.Warm, Tier.Cold];

    /// <summary>
    /// Everything the scan needs, read inside one read transaction so a concurrent move of a memory between tiers is
    /// seen once and never as a deletion. The transaction always ends before this returns or throws.
    /// </summary>
    internal Snapshot ReadSnapshot()
    {
        Exec("BEGIN");
        try
        {
            var snapshot = ReadSnapshotCore();
            Exec("COMMIT");
            return snapshot;
        }
        catch
        {
            // Whatever failed, the read transaction ends here, so the connection is never left inside one.
            try { Exec("ROLLBACK"); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            throw;
        }
    }

    private Snapshot ReadSnapshotCore()
    {
        var entries = new SortedDictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        var knowledge = new HashSet<string>(StringComparer.Ordinal);
        var skippedIds = new HashSet<string>(StringComparer.Ordinal);
        var issues = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ineligible = new List<IneligibleEntry>();
        var skipped = new List<SkippedItem>();

        var machineText = Meta(SyncIdentity.MetaKeys.Machine);
        var machine = machineText is null ? null : SyncStamp.MachineIdModule.tryCreate(machineText);
        if (machine is null)
            return new Snapshot(null, new(StringComparer.Ordinal), entries, knowledge, skippedIds, issues, ineligible, skipped, null, null);

        var clockText = Meta(SyncIdentity.MetaKeys.Hlc);
        var warningText = Meta(SyncIdentity.MetaKeys.ClockWarning);
        var rows = GitSyncRows.ReadAll(_conn);

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT entry_id, detail FROM git_sync_issue WHERE kind = 'ineligible-id'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                issues[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        }

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var readable = new Dictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        var unreadable = new Dictionary<string, string>(StringComparer.Ordinal);
        var ineligibleSeen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var tier in Tiers)
        {
            ReadMemoryTable(tier, occurrences, readable, unreadable, ineligibleSeen);
            if (tier.IsHot)
                AfterFirstTableRead?.Invoke();
        }

        foreach (var (id, count) in occurrences)
        {
            if (count > 1)
            {
                skippedIds.Add(id);
                skipped.Add(new SkippedItem(id, null, "the id is in more than one tier"));
            }
            else if (unreadable.TryGetValue(id, out var reason))
            {
                skippedIds.Add(id);
                skipped.Add(new SkippedItem(id, null, reason));
            }
            else
            {
                entries[id] = readable[id];
            }
        }
        foreach (var (id, tierName) in ineligibleSeen)
        {
            skippedIds.Add(id);
            ineligible.Add(new IneligibleEntry(id, tierName));
        }
        ineligible.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        skipped.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        foreach (var tier in Tiers)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT id FROM {MigrationRunner.TableName(tier, ContentType.Knowledge)}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                knowledge.Add(r.GetString(0));
        }

        return new Snapshot(machineText, rows, entries, knowledge, skippedIds, issues, ineligible, skipped, clockText, warningText);
    }

    private void ReadMemoryTable(
        Tier tier,
        Dictionary<string, int> occurrences,
        Dictionary<string, SnapshotEntry> readable,
        Dictionary<string, string> unreadable,
        Dictionary<string, string> ineligibleSeen)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {MigrationRunner.TableName(tier, ContentType.Memory)}";
        using var reader = cmd.ExecuteReader();
        var tierName = SyncRecordModule.tierName(tier);
        while (reader.Read())
        {
            // The id and the tier first, so an entry that cannot be mapped still counts as existing.
            var id = reader.GetString(reader.GetOrdinal("id"));
            if (!SyncValidation.isEligibleId(id))
            {
                ineligibleSeen.TryAdd(id, tierName);
                continue;
            }
            occurrences[id] = occurrences.GetValueOrDefault(id) + 1;
            try
            {
                var entry = SqliteStore.RowToEntry(reader);
                var sticky = tier.IsHot && reader.GetInt64(reader.GetOrdinal("sticky")) != 0;
                readable[id] = new SnapshotEntry(LocalEntryReader.ToLocalEntry(entry, tier, sticky), tier);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                unreadable.TryAdd(id, $"the entry cannot be read: {ex.Message}");
            }
        }
    }

    private string? Meta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
