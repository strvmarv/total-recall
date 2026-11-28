using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// The limits the apply needs from the configuration. A <see cref="HotMaxContentChars"/> of 0 or less means no limit.
/// <see cref="DeleteGuardPercent"/> is the share of the active rows one direction of deletions may reach before the pull
/// holds them (milestone 1b-ii, decision 3: the limit is <c>max(10, active rows * percent / 100)</c>); the default is the
/// configuration's default.
/// </summary>
public sealed record ApplyOptions(int HotMaxContentChars, int DeleteGuardPercent = 20);

public sealed record PostponedEntry(string Id, string Reason);

public sealed record DeferredEntry(string Id, string Reason);

public sealed record FailedEntry(string Id, int Attempts, string Detail);

/// <summary>A path the pull did not read as a record, with its issue key and the reason word.</summary>
public sealed record RejectedItem(string Key, string Reason);

/// <summary>A deletion the pull held back: the entry's id, the direction (<c>remote</c> or <c>local</c>) and the cause (<c>guard</c>, <c>unproven</c> or <c>sticky</c>).</summary>
public sealed record HeldEntry(string Id, string Direction, string Cause);

/// <summary>A direction whose deletions went over the guard limit: how many there were and the limit.</summary>
public sealed record GuardTrip(string Direction, int Count, int Limit);

/// <summary>An entry the pull has a one-line note about.</summary>
public sealed record NoteEntry(string Id, string Note);

/// <summary>
/// An entry whose record the next push writes again, with the groups (<c>content,sticky,tier</c>) marked for it: a pull lists
/// every entry it decided whose decision sets a marker, also when the row already held it (milestone 1b-ii, D16).
/// </summary>
public sealed record RepushEntry(string Id, string Groups);

/// <summary>
/// What one pull did. <see cref="Complete"/> is true when every id of the set was processed and none was deferred.
/// <see cref="Postponed"/> is always empty: no decision is postponed any more (conflict review D3); it stays for the shape
/// of the report.
/// <see cref="Held"/> lists every held-delete item the pull wrote, the fallback holds of an unproven pull included: a
/// deletion candidate whose entry is also in <see cref="Failed"/> or <see cref="Skipped"/> is held all the same, and one
/// whose hold could not be written is in <see cref="Deferred"/> (milestone 1b-ii, D5).
/// </summary>
public sealed record ApplySummary(
    int Inserted,
    int Updated,
    int RowsDropped,
    int Unchanged,
    int NotAttempted,
    bool Complete,
    string Head,
    bool Proven,
    IReadOnlyList<PostponedEntry> Postponed,
    IReadOnlyList<DeferredEntry> Deferred,
    IReadOnlyList<FailedEntry> Failed,
    IReadOnlyList<RejectedItem> Rejected,
    IReadOnlyList<SkippedItem> Skipped,
    IReadOnlyList<string> ColderTier,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<HeldEntry> Held,
    IReadOnlyList<GuardTrip> GuardTripped,
    IReadOnlyList<string> PushDeletions,
    IReadOnlyList<string> ClosedHeld,
    IReadOnlyList<NoteEntry> Notes,
    IReadOnlyList<RepushEntry> RepushMarked);

public abstract record ApplyOutcome
{
    private ApplyOutcome() { }

    public sealed record Done(ApplySummary Summary) : ApplyOutcome;

    /// <summary>The clone or the database could not be read; nothing was changed by the failing step (entries already done stay done).</summary>
    public sealed record Failed(string Reason) : ApplyOutcome;
}

/// <summary>
/// The apply of the pull (milestone 1b-i-2): reads the repository tree at the fetched head and carries the pure merge
/// decisions out on this machine's memories, one entry in one immediate transaction at a time. Milestone 1b-ii carries
/// deletions out (D6 to D13): a proven remote deletion is applied, one that is unproven or would remove a pin is held as an
/// open <c>held-delete</c> item, and a pending text edit beats it; a proven local deletion is listed for the push
/// (<see cref="ApplySummary.PushDeletions"/>), an unproven one is held, and one whose record in <c>R</c> is rejected or
/// invalid is skipped. Before the first entry a preview counts each direction's deletions (D1 to D4); a direction over
/// the limit carries none of them out and holds each with the cause <c>guard</c> (<see cref="ApplySummary.GuardTripped"/>),
/// and the pull is still complete. A record whose stamp for a group is older than the base's (a reset reverted it) leaves
/// the entry alone and marks the group in the row's <c>repush_groups</c> for the push (D16); a marked row is decided on every
/// pull. No decision is postponed (D19; pin review D3; conflict review D3): a decision with a <c>NewPin</c> or <c>PinEdit</c>
/// request is carried out and writes its pin item by the arrival rule, and a text conflict (FC4, FC5, NC4) applies or keeps
/// the clock's winner and files the losing text as a <c>conflict</c> item in the same transaction. In an unproven pull a
/// deletion candidate whose entry failed or was skipped is still held before <c>L</c> moves (D5). An insert (W1) whose
/// record's origin hash maps in <c>import_log</c> to a different existing memory is skipped and recorded in
/// <c>git_sync_alias</c> (alias D1). A memory whose record disappeared under an open alias converges onto the canonical
/// record in one transaction (alias D3): it is re-keyed and the canonical record is applied as a first sync; it is never
/// read as a deletion. Nothing calls it yet.
/// </summary>
public sealed partial class GitSyncApplier
{
    private readonly GitClient _client;
    private readonly SqliteStore _store;
    private readonly IEmbedder _embedder;
    private readonly ApplyOptions _options;
    private readonly Func<long> _nowMs;

    public GitSyncApplier(GitClient client, SqliteStore store, IEmbedder embedder, ApplyOptions options, Func<long> nowMs)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    }

    /// <summary>Test seam: called with the id before the decision's inputs are read.</summary>
    internal Action<string>? BeforeEntry { get; set; }

    /// <summary>Test seam: called with the id after the decision and the vector, before <c>BEGIN IMMEDIATE</c> (a second connection edits here).</summary>
    internal Action<string>? BeforeTransaction { get; set; }

    /// <summary>
    /// Test seam: called with the id and a step name inside the open transaction. The store transaction's own steps
    /// (<c>insert-row</c>, <c>update</c>, <c>move-copy</c>, <c>replace-vec</c>, <c>delete-vec</c>, <c>log-import</c>, ...) are
    /// forwarded, and the applier adds <c>reread</c>, <c>row</c>, <c>close</c> (a held-delete item removed), <c>hold</c> (one
    /// written), <c>issue</c> and <c>commit</c>; <c>hold-unproven</c> is the fallback hold of an unproven pull, in its own
    /// transaction after the entry's. <c>alias</c> writes the open rows of an alias skip (alias D1). A convergence (alias D3)
    /// adds <c>rekey</c> (the entry and its conflict item re-keyed, with the store's <c>rekey-row</c> and <c>rekey-parent</c>)
    /// and <c>converge</c> (the other id's row, issue and pin items removed, its <c>import_log</c> rows retargeted, the alias
    /// resolved); its steps are reported with the other id, the id the pull visited.
    /// </summary>
    internal Action<string, string>? BeforeEntryStep { get; set; }

    /// <summary>How many entry transactions were opened (the small clean-up ones and a row drop included).</summary>
    internal int TransactionCount { get; private set; }
}
