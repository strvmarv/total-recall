using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>The limits the apply needs from the configuration. A <see cref="HotMaxContentChars"/> of 0 or less means no limit.</summary>
public sealed record ApplyOptions(int HotMaxContentChars);

public sealed record PostponedEntry(string Id, string Reason);

public sealed record DeferredEntry(string Id, string Reason);

public sealed record FailedEntry(string Id, int Attempts, string Detail);

/// <summary>A path the pull did not read as a record, with its issue key and the reason word.</summary>
public sealed record RejectedItem(string Key, string Reason);

/// <summary>What one pull did. <see cref="Complete"/> is true when every id of the set was processed and none was postponed or deferred.</summary>
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
    IReadOnlyList<string> ColderTier);

public abstract record ApplyOutcome
{
    private ApplyOutcome() { }

    public sealed record Done(ApplySummary Summary) : ApplyOutcome;

    /// <summary>The clone or the database could not be read; nothing was changed by the failing step (entries already done stay done).</summary>
    public sealed record Failed(string Reason) : ApplyOutcome;
}

/// <summary>
/// The apply of the pull (milestone 1b-i-2): reads the repository tree at the fetched head and carries the pure merge
/// decisions out on this machine's memories, one entry in one immediate transaction at a time. A decision that needs a
/// part not built yet (a deletion, a review item, a re-push) is postponed and changes nothing. Nothing calls it yet.
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
    /// (<c>insert-row</c>, <c>update</c>, <c>move-copy</c>, <c>replace-vec</c>, <c>log-import</c>, ...) are forwarded, and the
    /// applier adds <c>reread</c>, <c>row</c>, <c>issue</c> and <c>commit</c>.
    /// </summary>
    internal Action<string, string>? BeforeEntryStep { get; set; }

    /// <summary>How many entry transactions were opened (the small clean-up ones and a row drop included).</summary>
    internal int TransactionCount { get; private set; }

    public Task<ApplyOutcome> ApplyAsync(FetchResult fetch, ScanSummary scan, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("the whole pull is built in a later task of the milestone");
}
