using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Diagnostics;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The embed side of the archive store (search the archive, spec sections 2 and 3): the work queue, the
/// one-transaction vector write, the lease, the fingerprint, the counts and the last-run record. All SQL is
/// parameterised; nothing here catches (the worker owns the failure policy) except the restore of the busy timeout
/// after a lease release.
/// </summary>
public sealed partial class TranscriptStore
{
    private const int VectorDimensions = 384;
    private const long LeaseStaleMs = 10L * 60 * 1000;

    private const string LeaseKey = "embed.lease";
    private const string LastRunKey = "embed.last_run";
    private const string ProviderKey = "embed.provider";
    private const string ModelKey = "embed.model";
    private const string RevisionKey = "embed.revision";
    private const string DimensionsKey = "embed.dimensions";

    // ------------------------------------------------------------------
    // Queue
    // ------------------------------------------------------------------

    internal const string HasWaitingEmbedWorkSql =
        "SELECT 1 FROM tx_chunk WHERE session_id = (SELECT id FROM tx_session WHERE host = $host AND host_session_id = $hid) " +
        "AND embedded = 0 AND embed_attempts < 3 LIMIT 1";

    internal static string BuildSessionFirstSql(int excludeCount) =>
        "SELECT id, content, embed_attempts FROM tx_chunk WHERE session_id = " +
        "(SELECT id FROM tx_session WHERE host = $host AND host_session_id = $hid) " +
        "AND embedded = 0 AND embed_attempts < 3" + ExcludeClause(excludeCount) + " ORDER BY id LIMIT $limit";

    internal static string BuildPlainSql(int excludeCount) =>
        "SELECT id, content, embed_attempts FROM tx_chunk WHERE embedded = 0 AND embed_attempts < 3" +
        ExcludeClause(excludeCount) + " ORDER BY id LIMIT $limit";

    private static string ExcludeClause(int count)
    {
        if (count <= 0)
            return string.Empty;
        var sb = new StringBuilder(" AND id NOT IN (");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("$x").Append(i.ToString(CultureInfo.InvariantCulture));
        }
        return sb.Append(')').ToString();
    }

    public bool HasWaitingEmbedWork(string host, string hostSessionId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostSessionId);
        using var cmd = Command(null, HasWaitingEmbedWorkSql, ("$host", host), ("$hid", hostSessionId));
        return cmd.ExecuteScalar() is not null;
    }

    public IReadOnlyList<EmbedChunk> NextEmbedWork(
        int limit, IReadOnlyCollection<long> exclude, (string Host, string HostSessionId)? preferSession = null)
    {
        ArgumentNullException.ThrowIfNull(exclude);
        if (limit <= 0)
            return Array.Empty<EmbedChunk>();

        var result = new List<EmbedChunk>();
        IReadOnlyCollection<long> rest = exclude;
        if (preferSession is { } prefer)
        {
            // Read one: the session's own waiting chunks (the (session_id, ...) unique index), in id order.
            var parameters = new List<(string, object)> { ("$host", prefer.Host), ("$hid", prefer.HostSessionId) };
            AddExcludeParameters(parameters, exclude);
            parameters.Add(("$limit", (long)limit));
            ReadChunks(BuildSessionFirstSql(exclude.Count), parameters, result);

            if (result.Count >= limit)
                return result;

            // Read two: the rest of the whole queue, never a chunk that read one returned.
            if (result.Count > 0)
            {
                var combined = new List<long>(exclude.Count + result.Count);
                combined.AddRange(exclude);
                foreach (var c in result)
                    combined.Add(c.Id);
                rest = combined;
            }
        }

        var restParameters = new List<(string, object)>();
        AddExcludeParameters(restParameters, rest);
        restParameters.Add(("$limit", (long)(limit - result.Count)));
        ReadChunks(BuildPlainSql(rest.Count), restParameters, result);
        return result;
    }

    private static void AddExcludeParameters(List<(string, object)> parameters, IReadOnlyCollection<long> exclude)
    {
        var i = 0;
        foreach (var id in exclude)
        {
            parameters.Add(("$x" + i.ToString(CultureInfo.InvariantCulture), id));
            i++;
        }
    }

    private void ReadChunks(string sql, List<(string, object)> parameters, List<EmbedChunk> into)
    {
        using var cmd = Command(null, sql, parameters.ToArray());
        using var r = cmd.ExecuteReader();
        while (r.Read())
            into.Add(new EmbedChunk(r.GetInt64(0), r.GetString(1)) { Attempts = r.GetInt32(2) });
    }

    // ------------------------------------------------------------------
    // The one-transaction write
    // ------------------------------------------------------------------

    public EmbedBatchOutcome StoreEmbeddings(
        IReadOnlyList<ChunkEmbedding> batch, ArchiveFingerprint current, EmbedLeaseHolder holder, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(holder);

        // Before any transaction: a wrong-sized vector can never be stored (the table is float[384]).
        foreach (var item in batch)
        {
            if (item.Vector is null || item.Vector.Length != VectorDimensions)
                throw new ArgumentException(
                    $"The vector of chunk {item.Id} has {item.Vector?.Length ?? 0} floats; the archive holds {VectorDimensions}.",
                    nameof(batch));
        }

        using var tx = _conn.BeginTransaction(deferred: false); // BEGIN IMMEDIATE

        // (a) the lease, then the stamp decision, both before anything is written
        if (!LeaseCarriesRun(tx, holder.RunId))
            return new EmbedBatchOutcome(EmbedBatchKind.LeaseLost, 0, 0);

        var stored = ReadFingerprintCore(tx);
        var vectorsExist = VectorsExist(tx);
        if (vectorsExist && stored is not null && stored != current)
            return new EmbedBatchOutcome(EmbedBatchKind.FingerprintMismatch, 0, 0);
        var stamp = stored is null || !vectorsExist;

        // (b) per chunk, the live rows decide: content equality guards the flag, the scope columns come from the join
        var written = 0;
        var skipped = 0;
        foreach (var item in batch)
        {
            var changed = Exec(tx,
                "UPDATE tx_chunk SET embedded = 1 WHERE id = $id AND embedded = 0 AND content = $content",
                ("$id", item.Id), ("$content", item.Content));
            if (changed == 0)
            {
                skipped++;
                continue;
            }

            Exec(tx,
                "INSERT INTO tx_chunk_vec(rowid, session_id, project, kind, ts, embedding) " +
                "SELECT c.id, c.session_id, s.project_key, s.kind, COALESCE(CAST(strftime('%s', c.ts) AS INTEGER), 0), $embedding " +
                "FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id WHERE c.id = $id",
                ("$id", item.Id), ("$embedding", MemoryMarshal.AsBytes(new ReadOnlySpan<float>(item.Vector)).ToArray()));
            written++;
        }

        // (c) stamp, (d) renew the lease
        if (stamp)
        {
            WriteState(tx, ProviderKey, current.Provider);
            WriteState(tx, ModelKey, current.Model);
            WriteState(tx, RevisionKey, current.Revision);
            WriteState(tx, DimensionsKey, current.Dimensions.ToString(CultureInfo.InvariantCulture));
        }
        WriteState(tx, LeaseKey, LeaseValue(holder, now));

        tx.Commit();
        return new EmbedBatchOutcome(EmbedBatchKind.Stored, written, skipped);
    }

    public bool RecordEmbedFailure(long chunkId, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        // Not a content change (content is not in the SET list), so the reset trigger does not fire.
        return Exec(null,
            "UPDATE tx_chunk SET embed_attempts = embed_attempts + 1 WHERE id = $id AND embedded = 0 AND content = $content",
            ("$id", chunkId), ("$content", content)) > 0;
    }

    public int ResetEmbedAttempts() =>
        Exec(null, "UPDATE tx_chunk SET embed_attempts = 0 WHERE embed_attempts <> 0");

    // ------------------------------------------------------------------
    // Lease: "<machine>:<pid>:<run id>:<unix ms>" in tx_state
    // ------------------------------------------------------------------

    public bool TryAcquireEmbedLease(EmbedLeaseHolder holder, DateTimeOffset now, Func<string, int, bool> holderIsAlive)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(holderIsAlive);

        using var tx = _conn.BeginTransaction(deferred: false); // read, decide and write under one write lock
        var existing = ReadState(tx, LeaseKey);
        var take = existing is null || !TryParseLease(existing, out var machine, out var pid, out var runId, out var atMs)
            || runId == holder.RunId
            || now.ToUnixTimeMilliseconds() - atMs > LeaseStaleMs
            || (string.Equals(machine, holder.Machine, StringComparison.Ordinal) && !holderIsAlive(machine, pid));
        if (!take)
            return false;

        WriteState(tx, LeaseKey, LeaseValue(holder, now));
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Releases the lease while it still carries this run's id. On a connection opened with a shorter busy timeout (the
    /// sync's 500 ms) the release waits up to <see cref="ReleaseBusyTimeoutMs"/> for the write lock: a release that gave
    /// up while another writer held the lock would leave the lease to refuse every run of this engine for ten minutes.
    /// The connection's own timeout is restored afterwards (a failed restore is logged, never thrown).
    /// </summary>
    public void ReleaseEmbedLease(EmbedLeaseHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        var original = ReadBusyTimeoutMs();
        var raise = original < ReleaseBusyTimeoutMs;
        if (raise)
            SetBusyTimeout(ReleaseBusyTimeoutMs);
        try
        {
            using var tx = _conn.BeginTransaction(deferred: false);
            if (LeaseCarriesRun(tx, holder.RunId))
                Exec(tx, "DELETE FROM tx_state WHERE key = $k", ("$k", LeaseKey));
            tx.Commit();
        }
        finally
        {
            if (raise)
            {
                try { SetBusyTimeout(original); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ExceptionLogger.LogChain("[total-recall] transcripts: could not restore busy_timeout after the lease release", ex);
                }
            }
        }
    }

    /// <summary>
    /// The longest the lease release waits for the write lock (SQLite's own busy wait, which the driver's shorter
    /// command timeout does not cut short), so a sync that fails busy still frees the lease within about 4 s.
    /// </summary>
    internal const int ReleaseBusyTimeoutMs = 4000;

    private static string LeaseValue(EmbedLeaseHolder h, DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"{h.Machine}:{h.Pid}:{h.RunId}:{now.ToUnixTimeMilliseconds()}");

    /// <summary>Parses from the right, so a machine name that holds a colon still works.</summary>
    private static bool TryParseLease(string value, out string machine, out int pid, out string runId, out long atMs)
    {
        machine = string.Empty;
        runId = string.Empty;
        pid = 0;
        atMs = 0;
        var i3 = value.LastIndexOf(':');
        if (i3 <= 0) return false;
        var i2 = value.LastIndexOf(':', i3 - 1);
        if (i2 <= 0) return false;
        var i1 = value.LastIndexOf(':', i2 - 1);
        if (i1 <= 0) return false;
        if (!long.TryParse(value.AsSpan(i3 + 1), NumberStyles.None, CultureInfo.InvariantCulture, out atMs)) return false;
        if (!int.TryParse(value.AsSpan(i1 + 1, i2 - i1 - 1), NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return false;
        machine = value[..i1];
        runId = value.Substring(i2 + 1, i3 - i2 - 1);
        return true;
    }

    private bool LeaseCarriesRun(SqliteTransaction tx, string runId) =>
        ReadState(tx, LeaseKey) is { } v
        && TryParseLease(v, out _, out _, out var stored, out _)
        && string.Equals(stored, runId, StringComparison.Ordinal);

    // ------------------------------------------------------------------
    // Fingerprint
    // ------------------------------------------------------------------

    public ArchiveFingerprint? ReadEmbedFingerprint()
    {
        using var tx = _conn.BeginTransaction(deferred: true);
        var result = ReadFingerprintCore(tx);
        tx.Commit();
        return result;
    }

    public FingerprintState CheckFingerprint(ArchiveFingerprint current)
    {
        ArgumentNullException.ThrowIfNull(current);
        using var tx = _conn.BeginTransaction(deferred: true);
        var stored = ReadFingerprintCore(tx);
        var state = stored is null || !VectorsExist(tx)
            ? FingerprintState.Unstamped
            : stored == current ? FingerprintState.Match : FingerprintState.Mismatch;
        tx.Commit();
        return state;
    }

    private ArchiveFingerprint? ReadFingerprintCore(SqliteTransaction tx)
    {
        var provider = ReadState(tx, ProviderKey);
        var model = ReadState(tx, ModelKey);
        var revision = ReadState(tx, RevisionKey);
        var dims = ReadState(tx, DimensionsKey);
        if (provider is null || model is null || revision is null || dims is null
            || !int.TryParse(dims, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
            return null;
        return new ArchiveFingerprint(provider, model, revision, d);
    }

    private bool VectorsExist(SqliteTransaction tx)
    {
        using var cmd = Command(tx, "SELECT rowid FROM tx_chunk_vec LIMIT 1");
        return cmd.ExecuteScalar() is not null;
    }

    // ------------------------------------------------------------------
    // Counts and the last run
    // ------------------------------------------------------------------

    public EmbeddingCounts CountEmbedding()
    {
        using var cmd = Command(null,
            "SELECT count(*), " +
            "COALESCE(SUM(embedded = 1), 0), " +
            "COALESCE(SUM(embedded = 0 AND embed_attempts < 3), 0), " +
            "COALESCE(SUM(embedded = 0 AND embed_attempts >= 3), 0) FROM tx_chunk");
        using var r = cmd.ExecuteReader();
        r.Read();
        return new EmbeddingCounts(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }

    public void WriteEmbedRun(EmbedRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, RootsWriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("at", TranscriptTime.Iso(run.At));
            w.WriteNumber("embedded", run.Embedded);
            w.WriteNumber("remaining", run.Remaining);
            w.WriteNumber("gaveUp", run.GaveUp);
            w.WriteString("state", run.State);
            w.WriteString("stateKind", run.StateKind);
            w.WriteString("provider", run.Provider);
            w.WriteString("model", run.Model);
            w.WriteString("stoppedBy", run.StoppedBy);
            w.WriteEndObject();
        }
        Exec(null,
            "INSERT INTO tx_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$k", LastRunKey), ("$v", Encoding.UTF8.GetString(buffer.ToArray())));
    }

    public EmbedRunRecord? ReadEmbedRun()
    {
        var json = ReadState(null, LastRunKey);
        if (json is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TranscriptTime.TryParse(JsonString(root, "at"), out var at)
                || !root.TryGetProperty("embedded", out var embedded) || !embedded.TryGetInt32(out var e)
                || !root.TryGetProperty("remaining", out var remaining) || !remaining.TryGetInt32(out var rem)
                || !root.TryGetProperty("gaveUp", out var gaveUp) || !gaveUp.TryGetInt32(out var g))
                return null;
            return new EmbedRunRecord(at, e, rem, g,
                JsonString(root, "state") ?? string.Empty, JsonString(root, "stateKind") ?? string.Empty,
                JsonString(root, "provider") ?? string.Empty, JsonString(root, "model") ?? string.Empty,
                JsonString(root, "stoppedBy") ?? string.Empty);
        }
        catch (JsonException)
        {
            return null; // a damaged record shows no last run
        }
    }

    private static string? JsonString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // ------------------------------------------------------------------
    // tx_state helpers
    // ------------------------------------------------------------------

    private string? ReadState(SqliteTransaction? tx, string key)
    {
        using var cmd = Command(tx, "SELECT value FROM tx_state WHERE key = $k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    private void WriteState(SqliteTransaction tx, string key, string value) =>
        Exec(tx,
            "INSERT INTO tx_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$k", key), ("$v", value));
}
