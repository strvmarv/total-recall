using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TotalRecall.Infrastructure.Memory;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>What one <see cref="TranscriptIngester.IngestSession"/> did with a session file.</summary>
internal enum SessionIngestStatus
{
    /// <summary>A session row was created.</summary>
    New,

    /// <summary>A stored session was resumed or rewritten.</summary>
    Updated,

    /// <summary>The file, its stat and the cutoff are as stored: not read.</summary>
    Unchanged,

    /// <summary>Forgotten (by id, project key or root), or older than the cutoff, or forgotten during the pass.</summary>
    Excluded,

    /// <summary>Nothing of the file is kept: nothing stored, or a stored row reset to empty.</summary>
    Empty,

    /// <summary>No complete first line yet (or a first line over the limit): left for a later pass.</summary>
    Skipped,

    /// <summary>The host replaced or truncated the file: the stored chunks are kept and nothing is ingested.</summary>
    Changed,

    /// <summary>The cutoff or the session row changed under two passes in a row: left for the next run.</summary>
    Retry,
}

/// <summary>The status of one session pass and the chunk counts of the write (zero unless the pass wrote).</summary>
internal sealed record SessionIngestResult(
    SessionIngestStatus Status,
    int ChunksNew,
    int ChunksChanged,
    int ChunksUnchanged,
    int ChunksRemoved,
    int SkippedLines)
{
    public static SessionIngestResult Of(SessionIngestStatus status, int skippedLines = 0) =>
        new(status, 0, 0, 0, 0, skippedLines);
}

/// <summary>
/// Ingests one session file into the archive (spec section 7, steps 1 to 6): the cheap exclusion and unchanged checks,
/// the head and project identity, the mode (fresh, resume from the byte watermark, rewritten, or a changed source that
/// is left alone), the read and build, and one <see cref="ITranscriptStore.ApplyIngest"/> that re-checks exclusion,
/// cutoff and prior state inside its transaction. This is the only code that writes the archive's sessions.
/// </summary>
internal sealed class TranscriptIngester
{
    private readonly ITranscriptStore _store;
    private readonly ITranscriptAdapter _adapter;
    private readonly ProjectResolver _resolver;
    private readonly string _userHome;

    public TranscriptIngester(ITranscriptStore store, ITranscriptAdapter adapter, ProjectResolver resolver, string userHome)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(userHome);
        _store = store;
        _adapter = adapter;
        _resolver = resolver;
        _userHome = userHome;
    }

    /// <summary>The stored <c>source_path</c> of a session, or null (how the backfill breaks duplicate-id ties).</summary>
    public string? StoredSourcePath(string hostSessionId)
    {
        ArgumentNullException.ThrowIfNull(hostSessionId);
        return _store.GetIngestState(_adapter.Host, hostSessionId)?.SourcePath;
    }

    /// <summary>
    /// One pass over <paramref name="source"/>, whose size and mtime were observed before this call (they are what is
    /// stored, so a file that grows during the read is read again next time). Exceptions propagate.
    /// </summary>
    public SessionIngestResult IngestSession(SessionSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();

        // Step 1: a forgotten session is not opened.
        if (_store.IsExcluded(_adapter.Host, source.HostSessionId, null, null))
            return SessionIngestResult.Of(SessionIngestStatus.Excluded);

        // Steps 2 to 6; a Stale or Conflict write is redone once from step 2, a second one is left for the next run.
        // Inside each pass, a resumed read that finds no unit or a changed kind is redone once from the start (the
        // earlier units' headers carry the kind), so one call reads the file at most four times: two passes of at most
        // two reads. It cannot loop, because the redo is a whole-file read and a whole-file read is final.
        var first = Pass(source, ct);
        if (first.Result is not null) return first.Result;
        ct.ThrowIfCancellationRequested();
        var second = Pass(source, ct);
        return second.Result ?? SessionIngestResult.Of(SessionIngestStatus.Retry, second.SkippedLines);
    }

    /// <summary>A finished result, or (null result) a write that found the cutoff or the row changed.</summary>
    private readonly record struct PassOutcome(SessionIngestResult? Result, int SkippedLines);

    private PassOutcome Pass(SessionSource source, CancellationToken ct)
    {
        var host = _adapter.Host;
        var id = source.HostSessionId;

        // Step 2: the cutoff first (it takes part in the unchanged check), then the stored state.
        var cutoff = _store.IngestNotBefore();
        var cutoffRaw = cutoff?.Raw;
        var state = _store.GetIngestState(host, id);
        if (state is not null
            && state.SourceSize == source.Size
            && state.SourceMtime == source.MtimeMs
            && string.Equals(state.BuiltUnderCutoff, cutoffRaw, StringComparison.Ordinal))
            return Done(SessionIngestStatus.Unchanged);
        if (cutoff is not null && MtimeAtOrBefore(source.MtimeMs, cutoff.At))
            return Done(SessionIngestStatus.Excluded);

        // Step 3: the head (the first read of the file), the project identity, the exclusion check with the key.
        var head = _adapter.ReadHead(source);
        if (head.Fingerprint is null)
            return Done(SessionIngestStatus.Skipped);

        var derived = ProjectIdentityRule.Derive(head.Cwd, ProjectDirName(source.Path), _resolver, _userHome);
        string key = derived.Key, keySource = derived.KeySource;
        var root = derived.Root;
        if (state is not null
            && state.ProjectKeySource == ProjectIdentityRule.SourceRemote
            && derived.KeySource == ProjectIdentityRule.SourcePath)
        {
            // Spec section 6 item 7: a path key never replaces a stored remote key; the root goes with the key.
            key = state.ProjectKey;
            keySource = state.ProjectKeySource;
            root = state.ProjectRoot;
        }
        var cwd = derived.Cwd;
        if (_store.IsExcluded(host, id, key, cwd))
            return Done(SessionIngestStatus.Excluded);

        // Step 4: the mode.
        if (state is not null && SourceChanged(state, head, source))
            return Done(SessionIngestStatus.Changed);
        var rewrite = state is null
            || state.ExchangeCount == 0
            || !string.Equals(state.BuiltUnderCutoff, cutoffRaw, StringComparison.Ordinal)
            || !string.Equals(state.ProjectKey, key, StringComparison.Ordinal);

        while (true)
        {
            // Step 5: read and build. A resumed pass re-reads the last stored unit and continues its numbering.
            var resume = !rewrite;
            var start = resume ? state!.IngestedOffset : 0;
            var firstUnit = resume ? state!.ExchangeCount - 1 : 0;
            var settledBefore = resume ? state!.SettledUserTurns : 0;

            var stats = new ReadStats();
            var build = ExchangeBuilder.Build(_adapter.ReadRecords(source, start, stats, ct), cutoff?.At, firstUnit);

            if (build.Units.Count == 0)
            {
                // A resumed pass always re-reads the last unit; finding none means the file is not what was stored.
                if (resume)
                {
                    rewrite = true;
                    continue;
                }
                if (state is null)
                    return Done(SessionIngestStatus.Empty, stats.SkippedLines);
                return Apply(Reset(source, head, state, key, keySource, root, cwd, cutoffRaw), SessionIngestStatus.Empty, stats);
            }

            var kind = SessionKindRule.Decide(head.Entrypoint, settledBefore, build.UserTurns, build.FirstUserText, state?.Kind);
            if (resume && !string.Equals(kind, state!.Kind, StringComparison.Ordinal))
            {
                // The earlier units' headers carry the old kind: redo once from the start (a whole-file read is final).
                rewrite = true;
                continue;
            }

            var last = build.Units[^1];
            var settled = settledBefore;
            for (var i = 0; i < build.Units.Count - 1; i++)
                settled += build.Units[i].UserTurns;

            string? title, startedAt, endedAt;
            if (resume)
            {
                title = state!.Title ?? (settledBefore == 0 ? SessionTitle.From(build.FirstUserText) : null);
                startedAt = state.StartedAt ?? IsoOrNull(build.FirstTs);
                endedAt = Later(state.EndedAt, build.LastTs);
            }
            else
            {
                title = SessionTitle.From(build.FirstUserText);
                startedAt = IsoOrNull(build.FirstTs);
                endedAt = IsoOrNull(build.LastTs);
            }

            var facts = new ChunkHeaderFacts(host, id, key, kind);
            var chunks = new List<IngestChunk>();
            foreach (var unit in build.Units)
                chunks.AddRange(TranscriptChunker.Chunk(unit, facts));

            var ingest = new SessionIngest(
                host, id, key, keySource, root, cwd, startedAt, endedAt, kind, title,
                source.Path, source.Size, source.MtimeMs, head.Fingerprint,
                last.StartOffset, last.Number + 1, settled, cutoffRaw,
                state, firstUnit, chunks);
            return Apply(ingest, null, stats);
        }
    }

    /// <summary>
    /// The row of a session that now keeps nothing: chunks removed, offset, counts, title and times cleared, the stored
    /// kind kept (it is NOT NULL), the new source fields and cutoff written. Read as a rewritten pass next time.
    /// </summary>
    private SessionIngest Reset(
        SessionSource source, SessionHead head, SessionIngestState state,
        string key, string keySource, string? root, string? cwd, string? cutoffRaw) =>
        new(_adapter.Host, source.HostSessionId, key, keySource, root, cwd, null, null, state.Kind, null,
            source.Path, source.Size, source.MtimeMs, head.Fingerprint,
            0, 0, 0, cutoffRaw, state, 0, Array.Empty<IngestChunk>());

    /// <summary>Step 6. <paramref name="appliedStatus"/> overrides New/Updated for a written pass (the reset is Empty).</summary>
    private PassOutcome Apply(SessionIngest ingest, SessionIngestStatus? appliedStatus, ReadStats stats)
    {
        var outcome = _store.ApplyIngest(ingest);
        switch (outcome.Kind)
        {
            case IngestOutcomeKind.Applied:
                var status = appliedStatus ?? (outcome.Created ? SessionIngestStatus.New : SessionIngestStatus.Updated);
                return new PassOutcome(new SessionIngestResult(status, outcome.ChunksNew, outcome.ChunksChanged,
                    outcome.ChunksUnchanged, outcome.ChunksRemoved, stats.SkippedLines), stats.SkippedLines);
            case IngestOutcomeKind.Excluded:
                return Done(SessionIngestStatus.Excluded, stats.SkippedLines);
            default:   // Stale or Conflict: nothing was written
                return new PassOutcome(null, stats.SkippedLines);
        }
    }

    private static PassOutcome Done(SessionIngestStatus status, int skippedLines = 0) =>
        new(SessionIngestResult.Of(status, skippedLines), skippedLines);

    /// <summary>
    /// Step 4's "source changed": another first line, a file shorter than what was stored, or a stored offset that is no
    /// longer at a line start.
    /// </summary>
    private bool SourceChanged(SessionIngestState state, SessionHead head, SessionSource source) =>
        !string.Equals(state.SourceFingerprint, head.Fingerprint, StringComparison.Ordinal)
        || (state.SourceSize is { } size && size > source.Size)
        || (state.IngestedOffset > 0 && !_adapter.IsLineStart(source, state.IngestedOffset));

    /// <summary>A file last written at or before the cutoff cannot hold a record after it.</summary>
    private static bool MtimeAtOrBefore(long mtimeMs, DateTimeOffset cutoff)
    {
        DateTimeOffset mtime;
        try { mtime = DateTimeOffset.FromUnixTimeMilliseconds(mtimeMs); }
        catch (ArgumentOutOfRangeException) { return false; }
        return mtime <= cutoff;
    }

    /// <summary>The encoded project directory: the name of the file's parent directory.</summary>
    private static string ProjectDirName(string path) =>
        Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty) ?? string.Empty;

    private static string? IsoOrNull(DateTimeOffset? t) => t is { } v ? TranscriptTime.Iso(v) : null;

    /// <summary>The later of the stored <c>ended_at</c> and this pass's last timestamp.</summary>
    private static string? Later(string? stored, DateTimeOffset? pass)
    {
        if (pass is not { } p) return stored;
        if (TranscriptTime.TryParse(stored, out var s) && s >= p) return stored;
        return TranscriptTime.Iso(p);
    }
}
