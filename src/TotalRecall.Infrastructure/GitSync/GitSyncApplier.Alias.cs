using Microsoft.FSharp.Collections;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// The target of a converging id (alias D3): the canonical id, its record as read at <c>R</c>, the record's blob id and the
/// group's origin hash. <paramref name="OtherRows"/> are the other ids, with their origin hash, of the <c>open</c> rows
/// <c>(CanonicalId, other)</c> that this pull's detections of the canonical id planned (D1), without the converging id
/// itself; the convergence writes those whose id still exists here as a memory, as a skip would.
/// </summary>
internal sealed record ConvergeTarget(
    string CanonicalId, ParsedRecord Record, string Sha, string Hash, IReadOnlyList<(string Other, string Hash)> OtherRows);

public sealed partial class GitSyncApplier
{
    /// <summary>
    /// What the alias pre-pass found (alias D2): the converging ids, each with its target, and the ignored ids. Neither is
    /// counted by the deletion preview, decided as a deletion, or held by the unproven fallback.
    /// </summary>
    private sealed record AliasPass(IReadOnlyDictionary<string, ConvergeTarget> Converging, IReadOnlySet<string> Ignored)
    {
        public static readonly AliasPass None = new(
            new Dictionary<string, ConvergeTarget>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        /// <summary>True when the id converges or is ignored.</summary>
        public bool Excludes(string id) => Converging.ContainsKey(id) || Ignored.Contains(id);
    }

    private static bool IsMappingFailure(Exception ex) => ex is FormatException or ArgumentException or InvalidCastException;

    /// <summary>
    /// The alias pre-pass (alias D2 and D3 conditions 1 to 7), before the deletion preview; it writes nothing. The local ids
    /// that meet conditions 1, 2 and 7 (a readable local memory, an <c>active</c> row with a base, no path in <c>R</c>, no
    /// held-delete item) take their candidates from their <c>open</c> alias rows and from the detections of this pull's
    /// arriving records (W1 candidates whose origin hash is one of the id's <c>import_log</c> hashes), each target is the
    /// lowest candidate whose record is accepted and parses, and <see cref="SyncAlias.choose"/> splits them into converging
    /// and ignored ids. The pre-pass reads the blob of a W1 candidate only (an arriving record with no row, not skipped by
    /// the scan, and no readable local memory under its id); only one whose origin hash is a hash of an id of Q (its
    /// <c>import_log</c> hashes or the hashes of its open rows) also gets the knowledge check (and has its unreadable local
    /// read counted), so a record that can alias nothing here never fails the pre-pass. A read it cannot make (a W1
    /// candidate's blob read that is not ok, a relevant W1 candidate whose local read or knowledge check cannot be made, a
    /// candidate canonical record that cannot be read (in an unproven pull, one lower than the id's target), a target whose free check throws) fails closed, by
    /// the kind of pull. In a <paramref name="proven"/> pull it ignores every id of Q (a target that cannot be checked is not
    /// free), which delays an ordinary deletion by one pull. In an unproven pull nothing converges, an id with a known
    /// candidate (an open row, or a readable detection) is ignored, never held (it converges or is ignored whatever the
    /// unknown), and an id with no known candidate gets nothing from the pre-pass, so the unproven hold of W6.5 applies to it
    /// as before (an ignore would let <c>L</c> advance past a deletion the next, proven pull would carry out). Blobs are read
    /// through <paramref name="blobs"/>, so no blob is read twice. It never stops the pull.
    /// </summary>
    private async Task<AliasPass> PrepareAliases(
        IReadOnlyList<string> ids,
        Dictionary<string, string> skippedByScan,
        TreeView view,
        Dictionary<string, GitSyncRow> rows,
        Dictionary<string, HeldItem> held,
        Dictionary<string, GitOutcome<byte[]>> blobs,
        bool proven,
        CancellationToken ct)
    {
        var conn = _store.Connection;

        // 1. Condition 2: an active row with a base whose record is absent from R. None: nothing can converge.
        var absent = ids
            .Where(id => rows.TryGetValue(id, out var r) && r.State.IsActive && r.BaseJson is not null && !view.Present.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (absent.Count == 0)
            return AliasPass.None;

        // 2. Conditions 7 and 1, and the candidates of the open rows (a NULL origin hash is never a candidate).
        var q = new List<string>();
        var candidates = new Dictionary<string, List<(string Canonical, string Hash)>>(StringComparer.Ordinal);
        foreach (var x in absent)
        {
            if (held.ContainsKey(x) || !SyncValidation.isEligibleId(x))
                continue;
            LocalRead read;
            try
            {
                read = ReadLocal(x);
            }
            catch (FormatException)
            {
                continue;
            }
            if (read is not LocalRead.Present)
                continue;
            q.Add(x);
            candidates[x] = GitSyncAliases.ReadOpenOf(conn, x)
                .Where(r => r.OriginHash is not null)
                .Select(r => (r.CanonicalId, r.OriginHash!))
                .ToList();
        }
        if (q.Count == 0)
            return AliasPass.None;

        // A read the pre-pass cannot make fails closed, by the kind of pull. Proven: every id of Q is ignored for this pull
        // and retried by the next. Unproven: `unknown` is set; an ignore there lets L advance past the vanished record, so
        // only ids with alias evidence may be ignored (see IgnoreKnown).
        var ignoreAll = new AliasPass(
            new Dictionary<string, ConvergeTarget>(StringComparer.Ordinal), q.ToHashSet(StringComparer.Ordinal));
        var unknown = false;

        async Task<GitOutcome<byte[]>> Blob(string id, AcceptedRecord record)
        {
            if (!blobs.TryGetValue(id, out var blob))
            {
                blob = await _client.CatBlobAsync(record.Sha, SyncValidation.maxFileBytes, ct).ConfigureAwait(false);
                blobs[id] = blob;
            }
            return blob;
        }

        // 3. The detections of this pull's W1 candidates (a detection counts like an open row, D2). The open rows each
        // detection would write are kept per canonical id: a convergence onto it writes them too (D1), as the canonical
        // id's own visit may defer or fail in this pull and nothing detects them once the canonical id exists here.
        var detectedRows = new Dictionary<string, List<(string Other, string Hash)>>(StringComparer.Ordinal);
        var hashesOfQ = q
            .SelectMany(x => GitSyncAliases.HashesOf(conn, x).Concat(candidates[x].Select(c => c.Hash)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!view.Accepted.TryGetValue(id, out var record) || rows.ContainsKey(id) || skippedByScan.ContainsKey(id)
                || !SyncValidation.isEligibleId(id))
                continue;
            // A readable local memory under the id: not a W1 candidate, so its blob is not read here.
            var local = TryReadLocal(id);
            if (local is LocalRead.Present)
                continue;
            var blob = await Blob(id, record).ConfigureAwait(false);
            if (!blob.IsOk)
            {
                // Its origin is unknown: any id of Q may have lost a candidate. Keep scanning: the others still count.
                if (proven)
                    return ignoreAll;
                unknown = true;
                continue;
            }
            if (RecordReader.Parse(blob.Value, record.Path).Parsed?.Record.Origin is not { } origin)
                continue; // a record that does not parse cannot be inserted either; one with no origin aliases nothing
            var hash = origin.Value.Hash;
            if (!hashesOfQ.Contains(hash))
                continue; // it can alias no id of Q: irrelevant, so its local state is not checked
            // Unreadable, in two tiers or a corrupt knowledge row: the entry's own visit skips it, so whether it is a W1
            // candidate (and what it detects) is unknown.
            var inKnowledge = local is LocalRead.Absent ? TryInKnowledge(id) : null;
            if (inKnowledge is null)
            {
                if (proven)
                    return ignoreAll;
                unknown = true;
                continue;
            }
            if (inKnowledge == true)
                continue; // a knowledge entry: not a W1 candidate
            if (GitSyncAliases.PlanSkip(conn, id, hash) is not { } detection)
                continue;
            var linked = false;
            foreach (var x in q)
                if (!string.Equals(x, detection.Canonical, StringComparison.Ordinal) && detection.Targets.Contains(x, StringComparer.Ordinal))
                {
                    candidates[x].Add((detection.Canonical, hash));
                    linked = true;
                }
            if (linked)
            {
                if (!detectedRows.TryGetValue(detection.Canonical, out var list))
                    detectedRows[detection.Canonical] = list = [];
                list.AddRange(detection.Inserts.Select(other => (other, hash)));
            }
        }
        if (unknown)
            return IgnoreKnown();

        // 4. The candidate canonical records accepted at R that parse. A read that is not ok is not "does not parse": in a
        // proven pull it ignores Q; in an unproven pull the record is unread (step 5 decides whether it matters).
        var parses = new Dictionary<string, (ParsedRecord Record, string Sha)>(StringComparer.Ordinal);
        var unread = new HashSet<string>(StringComparer.Ordinal);
        foreach (var canonical in candidates.Values.SelectMany(l => l.Select(c => c.Canonical)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!view.Accepted.TryGetValue(canonical, out var record))
                continue;
            var blob = await Blob(canonical, record).ConfigureAwait(false);
            if (!blob.IsOk)
            {
                if (proven)
                    return ignoreAll;
                if (blob.Failure.Failure == SyncGit.FailureKind.Cancelled || ct.IsCancellationRequested)
                    return AliasPass.None; // a cancelled read stops the pull anyway
                unread.Add(canonical);
                continue;
            }
            if (RecordReader.Parse(blob.Value, record.Path).Parsed is { } parsed)
                parses[canonical] = (parsed, record.Sha);
        }

        // 5. Each id's target (condition 3) and whether it is free here (condition 5). An unread candidate lower than the
        // target (or with no target) makes the target unknown; a free check that throws is "not free" in a proven pull and
        // unknown in an unproven one.
        var free = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var parsing = SetModule.OfSeq(parses.Keys);
        var choices = new List<SyncAlias.Candidate>();
        foreach (var x in q)
        {
            var list = candidates[x];
            if (list.Count == 0)
                continue;
            var target = SyncAlias.target(ListModule.OfSeq(list.Select(c => c.Canonical)), parsing);
            if (list.Any(c => unread.Contains(c.Canonical) && (target is null || string.CompareOrdinal(c.Canonical, target.Value) < 0)))
            {
                unknown = true;
                continue;
            }
            var isFree = false;
            if (target is not null)
            {
                var t = target.Value;
                if (!free.TryGetValue(t, out var known))
                    free[t] = known = IsFree(t);
                if (known is null && !proven)
                {
                    unknown = true;
                    continue;
                }
                isFree = known == true;
            }
            choices.Add(new SyncAlias.Candidate(x, target, isFree));
        }
        if (unknown)
            return IgnoreKnown();

        // 6. At most one member per canonical id converges (condition 6); the others are ignored.
        var choice = SyncAlias.choose(ListModule.OfSeq(choices));
        var converging = new Dictionary<string, ConvergeTarget>(StringComparer.Ordinal);
        foreach (var (x, t) in choice.Converging)
        {
            var (record, sha) = parses[t];
            var hash = candidates[x].First(c => string.Equals(c.Canonical, t, StringComparison.Ordinal)).Hash;
            var others = detectedRows.TryGetValue(t, out var rowsOfT)
                ? rowsOfT.Where(r => !string.Equals(r.Other, x, StringComparison.Ordinal)).Distinct().ToList()
                : [];
            converging[x] = new ConvergeTarget(t, record, sha, hash, others);
        }
        return new AliasPass(converging, choice.Ignored.ToHashSet(StringComparer.Ordinal));

        // An unproven pull with an unknown (a read it could not make): an id with a known candidate (an open row or a
        // readable detection) converges or is ignored whatever the unknown (SyncAlias.choose; the unknown changes only
        // which), so it is ignored and nothing converges in this pull. An id with no known candidate keeps the ordinary
        // rules, so the unproven hold of W6.5 applies to it.
        AliasPass IgnoreKnown() => new(
            new Dictionary<string, ConvergeTarget>(StringComparer.Ordinal),
            q.Where(x => candidates[x].Count > 0).ToHashSet(StringComparer.Ordinal));

        // Null when a check throws: unknown.
        bool? IsFree(string t)
        {
            try
            {
                return ReadLocal(t) is LocalRead.Absent && !InKnowledge(t) && !rows.ContainsKey(t) && !GitSyncReviews.HasAnyItem(conn, t);
            }
            catch (Exception ex) when (IsMappingFailure(ex))
            {
                return null;
            }
        }

        // Null when the local read throws.
        LocalRead? TryReadLocal(string id)
        {
            try
            {
                return ReadLocal(id);
            }
            catch (Exception ex) when (IsMappingFailure(ex))
            {
                return null;
            }
        }

        // Null when the knowledge check throws.
        bool? TryInKnowledge(string id)
        {
            try
            {
                return InKnowledge(id);
            }
            catch (Exception ex) when (IsMappingFailure(ex))
            {
                return null;
            }
        }
    }
}
