using System;
using System.Collections.Generic;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>A forgotten session's exclusion record: identifiers and the time, no text.</summary>
public sealed record ForgottenSession(string Host, string HostSessionId, string At);

/// <summary>A forgotten project's exclusion record: its key, the directories its removed sessions had, and the time.</summary>
public sealed record ForgottenProject(string ProjectKey, IReadOnlyList<string> Roots, string At);

/// <summary>
/// What <see cref="ITranscriptStore.Status"/> reports. <paramref name="Location"/> is the separate file's path, or
/// <c>main database (&lt;path&gt;)</c>. <paramref name="SessionsByKind"/> always holds <c>interactive</c> and
/// <c>automated</c>. <paramref name="IngestNotBefore"/> is the global cutoff (ISO-8601 UTC), or null.
/// </summary>
public sealed record TranscriptStatus(
    int SchemaVersion,
    string Location,
    long Sessions,
    long Chunks,
    IReadOnlyDictionary<string, long> SessionsByKind,
    IReadOnlyList<ForgottenSession> ForgottenSessions,
    IReadOnlyList<ForgottenProject> ForgottenProjects,
    string? IngestNotBefore)
{
    /// <summary>Sessions and chunks per project key and kind, most chunks first, then key, then kind; returned whole.</summary>
    public IReadOnlyList<ProjectKindCount> ByProjectAndKind { get; init; } = Array.Empty<ProjectKindCount>();
}

/// <summary>How many sessions and chunks a forget would remove (shown before the command asks).</summary>
public sealed record ForgetCount(long Sessions, long Chunks);

/// <summary>
/// What a forget removed. <paramref name="WalTruncated"/> is false when the write-ahead log could not be checkpointed
/// and truncated (another connection holds the database); then <paramref name="Warning"/> says the rows are deleted
/// but the log may still hold their text, and the caller must not report a clean delete.
/// </summary>
public sealed record ForgetResult(long Sessions, long Chunks, bool WalTruncated, string? Warning);

/// <summary>
/// The opt-in transcript archive (spec section 6). Owns its own connection. Every forget is one transaction followed by
/// the delete hygiene of section 7; exclusion records hold identifiers and directories, never conversation text.
/// </summary>
public interface ITranscriptStore : IDisposable
{
    TranscriptStatus Status();

    ForgetCount CountSession(string host, string hostSessionId);
    ForgetCount CountProject(string projectKey);
    ForgetCount CountAll();

    ForgetResult ForgetSession(string host, string hostSessionId);
    ForgetResult ForgetProject(string projectKey);
    ForgetResult ForgetAll(DateTimeOffset now);

    bool UnforgetSession(string host, string hostSessionId);
    bool UnforgetProject(string projectKey);
    bool ClearCutoff();

    /// <summary>
    /// True when a <c>session</c> record matches, or a <c>project</c> record matches by key or has a root that
    /// <paramref name="cwd"/> lies under (a directory-boundary prefix test, case-sensitive except on Windows).
    /// </summary>
    bool IsExcluded(string host, string hostSessionId, string? projectKey, string? cwd);

    /// <summary>The stored state of one session, or null when it has no row.</summary>
    SessionIngestState? GetIngestState(string host, string hostSessionId);

    /// <summary>The global ingest cutoff (parsed and as stored), or null when none is set.</summary>
    IngestCutoff? IngestNotBefore();

    /// <summary>
    /// Writes one session pass in one immediate transaction after checking, inside it, that the session is not
    /// excluded, the cutoff still equals the one the pass was built under and the session row still equals the pass's
    /// expected prior state. Anything else writes nothing and reports why.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Before any transaction: <c>FirstRebuiltUnit</c> is negative, a chunk lies below it, or two chunks share a unit and part.
    /// </exception>
    IngestOutcome ApplyIngest(SessionIngest ingest);

    /// <summary>True when a cutoff is set and <paramref name="timestamp"/> is earlier than it.</summary>
    bool IsBeforeCutoff(DateTimeOffset timestamp);

    // ------------------------------------------------------------------
    // The embed side (spec sections 2 and 3): queue, one-transaction vector write, lease, fingerprint, counts
    // ------------------------------------------------------------------

    /// <summary>
    /// Up to <paramref name="limit"/> chunks with <c>embedded = 0 AND embed_attempts &lt; 3</c> by ascending id,
    /// leaving out the ids in <paramref name="exclude"/>.
    /// </summary>
    IReadOnlyList<EmbedChunk> NextEmbedWork(int limit, IReadOnlyCollection<long> exclude);

    /// <summary>
    /// Writes the vectors of <paramref name="batch"/> and marks their chunks embedded in one immediate transaction: it
    /// checks the lease still carries <paramref name="holder"/>'s run id, decides whether to stamp
    /// <paramref name="current"/>, refuses (writing nothing) when vectors of another model exist, skips a chunk whose
    /// content changed or that is gone, takes the scope columns from the live session and chunk rows, stamps, and renews
    /// the lease time to <paramref name="now"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Before any transaction: a vector is not 384 floats long.</exception>
    EmbedBatchOutcome StoreEmbeddings(
        IReadOnlyList<ChunkEmbedding> batch, ArchiveFingerprint current, EmbedLeaseHolder holder, DateTimeOffset now);

    /// <summary>Adds one failed try to the chunk when it is still unembedded with this content; false otherwise.</summary>
    bool RecordEmbedFailure(long chunkId, string content);

    /// <summary>Sets every non-zero <c>embed_attempts</c> back to 0; returns how many chunks changed.</summary>
    int ResetEmbedAttempts();

    /// <summary>
    /// Takes the embed lease in one immediate transaction: free, stale (over 10 minutes), or held by a process of this
    /// machine that <paramref name="holderIsAlive"/> (called only for the same machine name) says is gone.
    /// </summary>
    bool TryAcquireEmbedLease(EmbedLeaseHolder holder, DateTimeOffset now, Func<string, int, bool> holderIsAlive);

    /// <summary>Deletes the lease only while it still carries <paramref name="holder"/>'s run id.</summary>
    void ReleaseEmbedLease(EmbedLeaseHolder holder);

    /// <summary>The stored fingerprint, or null when none (or an incomplete one) is stored.</summary>
    ArchiveFingerprint? ReadEmbedFingerprint();

    /// <summary>Unstamped, Match or Mismatch (spec section 2).</summary>
    FingerprintState CheckFingerprint(ArchiveFingerprint current);

    EmbeddingCounts CountEmbedding();

    void WriteEmbedRun(EmbedRunRecord run);

    /// <summary>The last embed run, or null when none was written or the record is unreadable.</summary>
    EmbedRunRecord? ReadEmbedRun();
}
