using System;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>The four facts that identify the embedder that made the archive's vectors (spec section 2).</summary>
public sealed record ArchiveFingerprint(string Provider, string Model, string Revision, int Dimensions);

/// <summary>How the stored fingerprint relates to the running embedder.</summary>
public enum FingerprintState
{
    /// <summary>None stored, or none that a vector row backs: no vector of another model can exist.</summary>
    Unstamped,

    /// <summary>The four facts are equal.</summary>
    Match,

    /// <summary>Vectors exist and any of the four facts differs.</summary>
    Mismatch,
}

/// <summary>A chunk waiting for a vector: its id and the text read for embedding.</summary>
public sealed record EmbedChunk(long Id, string Content)
{
    /// <summary>The chunk's failed tries so far (<c>embed_attempts</c> when it was read).</summary>
    public int Attempts { get; init; }
}

/// <summary>A chunk's vector with the content it was made from (the write checks the content is still current).</summary>
public sealed record ChunkEmbedding(long Id, string Content, float[] Vector);

/// <summary>What <see cref="ITranscriptStore.StoreEmbeddings"/> did.</summary>
public enum EmbedBatchKind
{
    /// <summary>The transaction committed (some chunks may have been skipped).</summary>
    Stored,

    /// <summary>The stored lease no longer carries the caller's run id; nothing was written.</summary>
    LeaseLost,

    /// <summary>Vectors of another model exist; nothing was written.</summary>
    FingerprintMismatch,
}

/// <summary><paramref name="Stored"/> chunks got a vector; <paramref name="Skipped"/> changed or vanished since they were read.</summary>
public sealed record EmbedBatchOutcome(EmbedBatchKind Kind, int Stored, int Skipped);

/// <summary>
/// Chunk counts for <c>status</c>. <paramref name="Waiting"/> is <c>embedded = 0 AND embed_attempts &lt; 3</c>,
/// <paramref name="GaveUp"/> is <c>embedded = 0 AND embed_attempts &gt;= 3</c>.
/// </summary>
public sealed record EmbeddingCounts(long Total, long Embedded, long Waiting, long GaveUp);

/// <summary>The identity of one embed run in the lease: machine name, process id and a random run id.</summary>
public sealed record EmbedLeaseHolder(string Machine, int Pid, string RunId);

/// <summary>
/// The last embed run, kept in <c>tx_state</c> for <c>status</c> (no text). <paramref name="State"/> is the gate's
/// text, <paramref name="StateKind"/> the state's name, <paramref name="Provider"/> and <paramref name="Model"/> the
/// embedder the run used.
/// </summary>
public sealed record EmbedRunRecord(
    DateTimeOffset At,
    int Embedded,
    int Remaining,
    int GaveUp,
    string State,
    string StateKind,
    string Provider,
    string Model,
    string StoppedBy);
