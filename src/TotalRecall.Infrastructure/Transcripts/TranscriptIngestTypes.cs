using System;
using System.Collections.Generic;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The stored state of one archived session as the ingester reads it (spec section 7 step 2). Value equality of the whole
/// record is the "prior state" check of <see cref="ITranscriptStore.ApplyIngest"/>.
/// </summary>
public sealed record SessionIngestState(
    long? SourceSize,
    long? SourceMtime,
    string? SourceFingerprint,
    long IngestedOffset,
    int ExchangeCount,
    int SettledUserTurns,
    string Kind,
    string ProjectKey,
    string ProjectKeySource,
    string? ProjectRoot,
    string? Cwd,
    string? Title,
    string? StartedAt,
    string? EndedAt,
    string? BuiltUnderCutoff,
    string? SourcePath);

/// <summary>The global ingest cutoff: the parsed time and the text stored in <c>tx_state</c>, which is compared verbatim.</summary>
public sealed record IngestCutoff(DateTimeOffset At, string Raw);

/// <summary>One chunk row to write: an exchange range, its part number and its (already redacted) text.</summary>
public sealed record IngestChunk(
    int ExchangeStart,
    int ExchangeEnd,
    int Part,
    string ChunkType,
    bool HasError,
    string? Ts,
    int RedactionVersion,
    string Content);

/// <summary>
/// One session pass for <see cref="ITranscriptStore.ApplyIngest"/>. <paramref name="ExpectedPrior"/> is the state the pass
/// started from (null: no row expected); <paramref name="FirstRebuiltUnit"/> is the first unit number whose chunks the pass
/// rebuilt (0: all of them).
/// </summary>
public sealed record SessionIngest(
    string Host,
    string HostSessionId,
    string ProjectKey,
    string ProjectKeySource,
    string? ProjectRoot,
    string? Cwd,
    string? StartedAt,
    string? EndedAt,
    string Kind,
    string? Title,
    string SourcePath,
    long SourceSize,
    long SourceMtime,
    string? SourceFingerprint,
    long IngestedOffset,
    int ExchangeCount,
    int SettledUserTurns,
    string? BuiltUnderCutoff,
    SessionIngestState? ExpectedPrior,
    int FirstRebuiltUnit,
    IReadOnlyList<IngestChunk> Chunks);

/// <summary>What <see cref="ITranscriptStore.ApplyIngest"/> did.</summary>
public enum IngestOutcomeKind
{
    /// <summary>The pass was written.</summary>
    Applied,

    /// <summary>The session or its project was forgotten since the pass started; nothing was written.</summary>
    Excluded,

    /// <summary>The cutoff changed since the pass was built; nothing was written.</summary>
    Stale,

    /// <summary>The session row is no longer the state the pass started from; nothing was written.</summary>
    Conflict,
}

/// <summary>The outcome of one <see cref="ITranscriptStore.ApplyIngest"/>; the counts are zero unless <c>Applied</c>.</summary>
public sealed record IngestOutcome(
    IngestOutcomeKind Kind,
    int ChunksNew = 0,
    int ChunksChanged = 0,
    int ChunksUnchanged = 0,
    int ChunksRemoved = 0,
    bool Created = false);

/// <summary>The sessions and chunks of one project key and session kind, for <c>transcripts status</c>.</summary>
public sealed record ProjectKindCount(string ProjectKey, string Kind, long Sessions, long Chunks);
