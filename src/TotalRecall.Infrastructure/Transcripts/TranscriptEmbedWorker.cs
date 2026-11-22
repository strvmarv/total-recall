using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// How much one run may do: <paramref name="MaxTime"/> (checked between batches, so a batch may overrun it) and
/// <paramref name="MaxChunks"/> (chunks read for embedding). Null means no limit.
/// </summary>
public sealed record EmbedBudget(TimeSpan? MaxTime, int? MaxChunks);

/// <summary>Why a run ended (spec section 3, item 4).</summary>
public enum EmbedStopReason
{
    /// <summary>No unembedded chunk is left (that this run has not already seen fail).</summary>
    Done,

    /// <summary>The time or chunk budget was used up.</summary>
    Budget,

    /// <summary>The cancellation token was set.</summary>
    Cancelled,

    /// <summary>The gate refused, or a gate condition was found during the run (model not ready, vectors of another model).</summary>
    Gate,

    /// <summary>The lease was held by another run, or was taken over during this one.</summary>
    Lease,

    /// <summary><c>embedding failed</c>, the dimension refusal, or five consecutive chunks that failed alone.</summary>
    Failed,
}

/// <summary>
/// What a run did. <paramref name="GaveUp"/> counts the chunks that reached their third failed try in this run;
/// <paramref name="Remaining"/> is the chunks still waiting (<c>embedded = 0 AND embed_attempts &lt; 3</c>) at the end.
/// </summary>
public sealed record EmbedRun(int Embedded, int Remaining, int GaveUp, EmbedGateResult State, EmbedStopReason StoppedBy);

/// <summary>Reported after every batch.</summary>
public sealed record EmbedProgress(int Embedded, int Remaining);

/// <summary>
/// Gives the archive's chunks their vectors (search the archive, spec section 3, items 1 to 7): one run at a time per
/// archive through the <c>embed.lease</c>, a probe before the first archive batch, batches of 16 embedded outside any
/// transaction and written in one transaction each, three failure classes with a single-chunk retry that uses the probe
/// as the health test, a budget checked between batches, and the last run's record. <see cref="Run"/> never throws;
/// every failure is logged through <see cref="ExceptionLogger.LogChain(string, Exception)"/> (stderr, never stdout).
/// </summary>
public sealed class TranscriptEmbedWorker
{
    /// <summary>Chunks read and written together.</summary>
    public const int BatchSize = 16;

    /// <summary>Chunks in a row that fail alone, with a healthy probe, before the run ends.</summary>
    public const int MaxConsecutiveLoneFailures = 5;

    /// <summary>Failed tries after which a chunk leaves the work list (the store's <c>embed_attempts &lt; 3</c>).</summary>
    private const int GiveUpAttempts = 3;

    private const string LogPrefix = "[total-recall] transcripts embed";

    private readonly ITranscriptStore _store;
    private readonly IEmbedder _embedder;
    private readonly TimeProvider _time;
    private readonly string _machine;
    private readonly int _pid;
    private readonly Func<string, int, bool> _holderIsAlive;
    private readonly Action? _betweenReadAndWrite;

    /// <param name="store">The archive; the worker uses only its embed members.</param>
    /// <param name="embedder">The embedder the run uses (not disposed here).</param>
    /// <param name="time">The clock for the budget, the lease time and the last run's time.</param>
    /// <param name="machineName">This machine's name in the lease; defaults to <see cref="Environment.MachineName"/>.</param>
    /// <param name="pid">This process's id in the lease; defaults to <see cref="Environment.ProcessId"/>.</param>
    /// <param name="holderIsAlive">
    /// Whether a lease holder of this machine still runs; defaults to a process check by pid.
    /// </param>
    /// <param name="betweenReadAndWrite">A test seam run after a batch is embedded and before it is written.</param>
    public TranscriptEmbedWorker(
        ITranscriptStore store,
        IEmbedder embedder,
        TimeProvider? time = null,
        string? machineName = null,
        int? pid = null,
        Func<string, int, bool>? holderIsAlive = null,
        Action? betweenReadAndWrite = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _time = time ?? TimeProvider.System;
        _machine = machineName ?? Environment.MachineName;
        _pid = pid ?? Environment.ProcessId;
        _holderIsAlive = holderIsAlive ?? ProcessIsAlive;
        _betweenReadAndWrite = betweenReadAndWrite;
    }

    /// <summary>
    /// Runs when <paramref name="gate"/> says ready: takes the lease, probes, embeds batch by batch until the budget,
    /// the cancellation or the work runs out, then writes <c>embed.last_run</c> and releases the lease. A refused gate
    /// does nothing (no lease, no record); a lease held by another run, or taken over during this one, ends with
    /// <see cref="EmbedStopReason.Lease"/> and writes no record (the holder's run owns it, and a run that lost the lease
    /// must not overwrite what the new holder wrote).
    /// </summary>
    public EmbedRun Run(
        EmbedGateResult gate,
        ArchiveFingerprint current,
        EmbedBudget budget,
        CancellationToken ct,
        Action<EmbedProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(budget);

        if (!gate.CanEmbed)
            return new EmbedRun(0, SafeRemaining(), 0, gate, EmbedStopReason.Gate);

        var holder = new EmbedLeaseHolder(_machine, _pid, Guid.NewGuid().ToString("N"));
        bool acquired;
        try
        {
            acquired = _store.TryAcquireEmbedLease(holder, _time.GetUtcNow(), _holderIsAlive);
        }
        catch (Exception ex)
        {
            ExceptionLogger.LogChain(LogPrefix + ": taking the lease", ex);
            return new EmbedRun(0, SafeRemaining(), 0, EmbedGate.Failed(ex.Message), EmbedStopReason.Failed);
        }

        if (!acquired)
            return new EmbedRun(0, SafeRemaining(), 0, EmbedGate.AnotherRun(), EmbedStopReason.Lease);

        var run = new RunState(gate, current, holder, ProviderOf(current), budget, ct, progress, _time.GetUtcNow());
        try
        {
            Loop(run);
        }
        catch (Exception ex)
        {
            // a store read, a failure count or the progress callback threw: nothing is counted for it
            ExceptionLogger.LogChain(LogPrefix, ex);
            run.End(EmbedGate.Failed(ex.Message), EmbedStopReason.Failed);
        }
        finally
        {
            run.Remaining = SafeRemaining();
            if (run.StoppedBy != EmbedStopReason.Lease)
            {
                try
                {
                    _store.WriteEmbedRun(new EmbedRunRecord(
                        _time.GetUtcNow(), run.Embedded, run.Remaining, run.GaveUp, run.State.Text, StateName(run.State.State),
                        current.Provider, current.Model, StopName(run.StoppedBy)));
                }
                catch (Exception ex)
                {
                    ExceptionLogger.LogChain(LogPrefix + ": writing the last run", ex);
                }
            }

            try
            {
                _store.ReleaseEmbedLease(holder); // deletes only while the lease still carries this run's id
            }
            catch (Exception ex)
            {
                ExceptionLogger.LogChain(LogPrefix + ": releasing the lease", ex);
            }
        }

        return new EmbedRun(run.Embedded, run.Remaining, run.GaveUp, run.State, run.StoppedBy);
    }

    // ------------------------------------------------------------------
    // The loop
    // ------------------------------------------------------------------

    private void Loop(RunState r)
    {
        var read = 0;
        while (true)
        {
            if (r.Ct.IsCancellationRequested)
            {
                r.End(r.Gate, EmbedStopReason.Cancelled);
                return;
            }

            if (BudgetSpent(r, read))
            {
                r.End(r.Gate, EmbedStopReason.Budget);
                return;
            }

            var limit = BatchSize;
            if (r.Budget.MaxChunks is int max)
                limit = Math.Min(limit, max - read);

            var work = _store.NextEmbedWork(limit, r.FailedAlone);
            if (work.Count == 0)
            {
                r.End(r.Gate, EmbedStopReason.Done);
                return;
            }

            // Once per run, before the first archive text is sent: the embedder must answer at all, and with 384 floats.
            if (!r.Probed)
            {
                try
                {
                    EmbedChecked(EmbedProbe.Text);
                }
                catch (Exception ex)
                {
                    EndOnEmbedderFailure(r, ex, "the probe");
                    return;
                }
                r.Probed = true;
            }

            read += work.Count;
            if (!EmbedBatch(r, work))
                return;

            if (r.Progress is { } report)
            {
                // Counted once per run, then followed locally: a chunk stored or given up on leaves the work list, one that
                // failed alone with tries left (or was skipped by a race) still waits. A full count per batch would scan the
                // table every 16 chunks. Ingest running at the same time is not seen here; the run's Remaining is fresh.
                r.WaitingAtStart ??= Waiting() + r.Embedded + r.GaveUp;
                report(new EmbedProgress(r.Embedded, Math.Max(0, r.WaitingAtStart.Value - r.Embedded - r.GaveUp)));
            }
        }
    }

    private bool BudgetSpent(RunState r, int read)
    {
        if (r.Budget.MaxChunks is int max && read >= max)
            return true;
        return r.Budget.MaxTime is TimeSpan t && _time.GetUtcNow() - r.Started >= t;
    }

    /// <summary>Embeds a batch outside any transaction, then writes it in one; false when the run ends.</summary>
    private bool EmbedBatch(RunState r, IReadOnlyList<EmbedChunk> work)
    {
        var vectors = new float[]?[work.Count];
        for (var i = 0; i < work.Count; i++)
        {
            try
            {
                vectors[i] = EmbedChecked(work[i].Content);
            }
            catch (Exception ex)
            {
                if (EmbedFailureClassifier.Classify(ex, r.Provider) != EmbedFailureClass.Chunk)
                {
                    EndOnEmbedderFailure(r, ex, $"chunk {work[i].Id}");
                    return false;
                }

                ExceptionLogger.LogChain($"{LogPrefix}: chunk {work[i].Id} (batch; retrying one chunk at a time)", ex);
                return RetryAlone(r, work, vectors);
            }
        }

        var batch = new List<ChunkEmbedding>(work.Count);
        for (var i = 0; i < work.Count; i++)
            batch.Add(new ChunkEmbedding(work[i].Id, work[i].Content, vectors[i]!));

        _betweenReadAndWrite?.Invoke();
        EmbedBatchOutcome outcome;
        try
        {
            outcome = _store.StoreEmbeddings(batch, r.Current, r.Holder, _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            if (!IsChunkWriteFailure(ex))
            {
                ExceptionLogger.LogChain(LogPrefix + ": writing a batch", ex);
                r.End(EmbedGate.Failed(ex.Message), EmbedStopReason.Failed);
                return false;
            }

            ExceptionLogger.LogChain(LogPrefix + ": writing a batch (retrying one chunk at a time)", ex);
            return RetryAlone(r, work, vectors);
        }

        return Apply(r, outcome);
    }

    /// <summary>
    /// The batch again, one chunk per embed and per transaction. Vectors already made in the batch are reused (each
    /// came back whole), so only the chunk that failed and those after it are sent again.
    /// </summary>
    private bool RetryAlone(RunState r, IReadOnlyList<EmbedChunk> work, float[]?[] vectors)
    {
        for (var i = 0; i < work.Count; i++)
        {
            var chunk = work[i];
            var vector = vectors[i];
            if (vector is null)
            {
                try
                {
                    vector = EmbedChecked(chunk.Content);
                }
                catch (Exception ex)
                {
                    if (EmbedFailureClassifier.Classify(ex, r.Provider) != EmbedFailureClass.Chunk)
                    {
                        EndOnEmbedderFailure(r, ex, $"chunk {chunk.Id}");
                        return false;
                    }
                    if (!FailedAlone(r, chunk, ex))
                        return false;
                    continue;
                }
            }

            _betweenReadAndWrite?.Invoke();
            EmbedBatchOutcome outcome;
            try
            {
                outcome = _store.StoreEmbeddings(
                    new[] { new ChunkEmbedding(chunk.Id, chunk.Content, vector) }, r.Current, r.Holder, _time.GetUtcNow());
            }
            catch (Exception ex)
            {
                if (!IsChunkWriteFailure(ex))
                {
                    ExceptionLogger.LogChain($"{LogPrefix}: writing chunk {chunk.Id}", ex);
                    r.End(EmbedGate.Failed(ex.Message), EmbedStopReason.Failed);
                    return false;
                }
                if (!FailedAlone(r, chunk, ex))
                    return false;
                continue;
            }

            if (!Apply(r, outcome))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A chunk failed alone: the probe decides whose failure it is. A failing probe ends the run with nothing counted;
    /// a healthy one counts a try against the chunk and leaves it out of the rest of the run. False when the run ends.
    /// </summary>
    private bool FailedAlone(RunState r, EmbedChunk chunk, Exception failure)
    {
        ExceptionLogger.LogChain($"{LogPrefix}: chunk {chunk.Id}", failure);
        try
        {
            EmbedChecked(EmbedProbe.Text);
        }
        catch (Exception probeFailure)
        {
            EndOnEmbedderFailure(r, probeFailure, "the probe after a chunk failure");
            return false;
        }

        r.FailedAlone.Add(chunk.Id);
        if (_store.RecordEmbedFailure(chunk.Id, chunk.Content) && chunk.Attempts + 1 >= GiveUpAttempts)
            r.GaveUp++;

        r.ConsecutiveLoneFailures++;
        if (r.ConsecutiveLoneFailures >= MaxConsecutiveLoneFailures)
        {
            r.End(
                EmbedGate.Failed(
                    $"{MaxConsecutiveLoneFailures} chunks in a row failed while the probe worked (last: {failure.Message})"),
                EmbedStopReason.Failed);
            return false;
        }
        return true;
    }

    private bool Apply(RunState r, EmbedBatchOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case EmbedBatchKind.Stored:
                r.Embedded += outcome.Stored;
                r.ConsecutiveLoneFailures = 0;
                return true;
            case EmbedBatchKind.LeaseLost:
                r.End(EmbedGate.AnotherRun(), EmbedStopReason.Lease);
                return false;
            default:
                r.End(EmbedGate.Mismatch(_store.ReadEmbedFingerprint(), r.Current), EmbedStopReason.Gate);
                return false;
        }
    }

    /// <summary>The run ends on an embedder failure that is not one chunk's (or a probe that failed).</summary>
    private void EndOnEmbedderFailure(RunState r, Exception ex, string what)
    {
        ExceptionLogger.LogChain($"{LogPrefix}: {what}", ex);
        if (ex is EmbedDimensionException dimension)
        {
            r.End(EmbedGate.Dimension(dimension.Actual), EmbedStopReason.Failed);
            return;
        }

        if (EmbedFailureClassifier.Classify(ex, r.Provider) == EmbedFailureClass.CannotRun)
        {
            r.End(EmbedGate.NotReady(ex.Message), EmbedStopReason.Gate);
            return;
        }

        r.End(EmbedGate.Failed(ex.Message), EmbedStopReason.Failed);
    }

    /// <summary>
    /// A write failure counts against a chunk only when SQLite rejected a value: SQLITE_ERROR (1), TOOBIG (18),
    /// CONSTRAINT (19) or MISMATCH (20), by primary code. Every other exception from the store (BUSY, LOCKED, FULL,
    /// READONLY, IOERR, CORRUPT, CANTOPEN, NOMEM, ...) ends the run uncounted as systemic, so a fault of the store never
    /// marks a chunk bad.
    /// </summary>
    private static bool IsChunkWriteFailure(Exception ex) =>
        ex is SqliteException sql && (sql.SqliteErrorCode & 0xFF) is SqliteError or SqliteTooBig or SqliteConstraint or SqliteMismatch;

    private const int SqliteError = 1;
    private const int SqliteTooBig = 18;
    private const int SqliteConstraint = 19;
    private const int SqliteMismatch = 20;

    /// <summary>Every returned vector is length-checked before anything is stored.</summary>
    private float[] EmbedChecked(string text)
    {
        var vector = _embedder.Embed(text);
        if (vector is null || vector.Length != EmbedGate.ArchiveDimensions)
            throw new EmbedDimensionException(vector?.Length ?? 0, EmbedGate.ArchiveDimensions);
        return vector;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private string ProviderOf(ArchiveFingerprint current)
    {
        try
        {
            return _embedder.Descriptor.Provider;
        }
        catch (Exception ex)
        {
            ExceptionLogger.LogChain(LogPrefix + ": reading the embedder's descriptor", ex);
            return current.Provider;
        }
    }

    private int Waiting() => (int)Math.Min(int.MaxValue, _store.CountEmbedding().Waiting);

    private int SafeRemaining()
    {
        try
        {
            return Waiting();
        }
        catch (Exception ex)
        {
            ExceptionLogger.LogChain(LogPrefix + ": counting the remaining chunks", ex);
            return 0;
        }
    }

    /// <summary>A same-machine holder is alive while a process with its pid exists (this process always is).</summary>
    private static bool ProcessIsAlive(string machine, int pid)
    {
        if (pid == Environment.ProcessId)
            return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false; // it exited
        }
        catch (Exception)
        {
            return true; // unknown: keep the lease; the ten-minute rule still frees it
        }
    }

    private static string StopName(EmbedStopReason reason) => reason switch
    {
        EmbedStopReason.Done => "done",
        EmbedStopReason.Budget => "budget",
        EmbedStopReason.Cancelled => "cancelled",
        EmbedStopReason.Gate => "gate",
        EmbedStopReason.Lease => "lease",
        _ => "failed",
    };

    private static string StateName(EmbedState state) => state switch
    {
        EmbedState.NotApplicable => nameof(EmbedState.NotApplicable),
        EmbedState.RemoteNotAllowed => nameof(EmbedState.RemoteNotAllowed),
        EmbedState.DimensionRefused => nameof(EmbedState.DimensionRefused),
        EmbedState.FingerprintMismatch => nameof(EmbedState.FingerprintMismatch),
        EmbedState.ModelNotReady => nameof(EmbedState.ModelNotReady),
        EmbedState.WaitingForReindex => nameof(EmbedState.WaitingForReindex),
        EmbedState.EmbeddingFailed => nameof(EmbedState.EmbeddingFailed),
        EmbedState.AnotherRunActive => nameof(EmbedState.AnotherRunActive),
        _ => nameof(EmbedState.Ready),
    };

    /// <summary>The mutable state of one run.</summary>
    private sealed class RunState
    {
        public RunState(
            EmbedGateResult gate,
            ArchiveFingerprint current,
            EmbedLeaseHolder holder,
            string provider,
            EmbedBudget budget,
            CancellationToken ct,
            Action<EmbedProgress>? progress,
            DateTimeOffset started)
        {
            Gate = gate;
            Current = current;
            Holder = holder;
            Provider = provider;
            Budget = budget;
            Ct = ct;
            Progress = progress;
            Started = started;
            State = gate;
        }

        public EmbedGateResult Gate { get; }
        public ArchiveFingerprint Current { get; }
        public EmbedLeaseHolder Holder { get; }
        public string Provider { get; }
        public EmbedBudget Budget { get; }
        public CancellationToken Ct { get; }
        public Action<EmbedProgress>? Progress { get; }
        public DateTimeOffset Started { get; }

        /// <summary>Ids that failed alone in this run; left out of every later read.</summary>
        public HashSet<long> FailedAlone { get; } = new();

        public bool Probed { get; set; }
        public int Embedded { get; set; }
        public int GaveUp { get; set; }
        public int Remaining { get; set; }
        public int ConsecutiveLoneFailures { get; set; }

        /// <summary>The waiting chunks at the start of the run (counted after the first batch, with that batch added back).</summary>
        public int? WaitingAtStart { get; set; }
        public EmbedGateResult State { get; private set; }
        public EmbedStopReason StoppedBy { get; private set; } = EmbedStopReason.Done;

        public void End(EmbedGateResult state, EmbedStopReason stoppedBy)
        {
            State = state;
            StoppedBy = stoppedBy;
        }
    }
}
