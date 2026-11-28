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
    /// and ignored ids. A W1 candidate's blob is read first; only one whose origin hash is a hash of an id of Q (its
    /// <c>import_log</c> hashes or the hashes of its open rows) gets the local read and knowledge check, so a candidate that
    /// can alias nothing here never fails the pre-pass. A read it cannot make (a blob read that is not ok, a relevant W1
    /// candidate whose local read or knowledge check cannot be made, a candidate canonical record that cannot be read, a
    /// target whose checks throw) fails closed, by the kind of pull: in a <paramref name="proven"/> pull it ignores every id
    /// of Q (a target that cannot be checked is not free), which delays an ordinary deletion by one pull; in an unproven pull
    /// it ignores nothing for the ids it leaves uncleared (every id of Q for a failed W1 blob read, else the ids whose
    /// candidates or target it leaves unknown), so the unproven hold of W6.5 applies to them as for any vanished record, and
    /// an ignore would let <c>L</c> advance past a deletion the next, proven pull then carries out. In such an unproven pull
    /// the cleared ids keep their ignore, and an id that would converge is ignored instead (another member of its group may
    /// be uncleared). Blobs are read through <paramref name="blobs"/>, so no blob is read twice. It never stops the pull.
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

        // Fail closed in a proven pull: every id of Q is kept for this pull and retried by the next. In an unproven pull an
        // ignore would advance L past the vanished record without a hold, and the next (proven) pull would delete it: the ids
        // a failed read leaves uncleared get nothing from the pre-pass, so the unproven hold applies to them.
        var ignoreAll = new AliasPass(
            new Dictionary<string, ConvergeTarget>(StringComparer.Ordinal), q.ToHashSet(StringComparer.Ordinal));
        var everyIdUncleared = proven ? ignoreAll : AliasPass.None;
        var uncleared = new HashSet<string>(StringComparer.Ordinal);

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
        var hashesOf = q.ToDictionary(
            x => x,
            x => GitSyncAliases.HashesOf(conn, x).Concat(candidates[x].Select(c => c.Hash)).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var hashesOfQ = hashesOf.Values.SelectMany(h => h).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!view.Accepted.TryGetValue(id, out var record) || rows.ContainsKey(id) || skippedByScan.ContainsKey(id)
                || !SyncValidation.isEligibleId(id))
                continue;
            var blob = await Blob(id, record).ConfigureAwait(false);
            if (!blob.IsOk)
                return everyIdUncleared; // its origin is unknown: any id of Q may have lost a candidate
            if (RecordReader.Parse(blob.Value, record.Path).Parsed?.Record.Origin is not { } origin)
                continue; // a record that does not parse cannot be inserted either; one with no origin aliases nothing
            var hash = origin.Value.Hash;
            if (!hashesOfQ.Contains(hash))
                continue; // it can alias no id of Q: irrelevant, so its local read and knowledge check are not made
            var entryHere = EntryHere(id);
            if (entryHere is null)
            {
                // Unreadable, in two tiers or a corrupt knowledge row: the entry's own visit skips it, so it is no proof of
                // anything, and the ids of Q that share its origin hash are not cleared.
                if (proven)
                    return ignoreAll;
                uncleared.UnionWith(q.Where(x => hashesOf[x].Contains(hash)));
                continue;
            }
            if (entryHere == true)
                continue; // a local memory or a knowledge entry: not a W1 candidate
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

        // 4. The candidate canonical records accepted at R that parse. A read that is not ok is not "does not parse": in a
        // proven pull it ignores Q; in an unproven pull the record is unknown (step 5 leaves the ids it may target uncleared).
        var parses = new Dictionary<string, (ParsedRecord Record, string Sha)>(StringComparer.Ordinal);
        var unread = new HashSet<string>(StringComparer.Ordinal);
        foreach (var canonical in candidates.Values.SelectMany(l => l.Select(c => c.Canonical)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!view.Accepted.TryGetValue(canonical, out var record))
                continue;
            var blob = await Blob(canonical, record).ConfigureAwait(false);
            if (!blob.IsOk)
            {
                if (proven || blob.Failure.Failure == SyncGit.FailureKind.Cancelled || ct.IsCancellationRequested)
                    return everyIdUncleared; // a cancelled read stops the pull anyway
                unread.Add(canonical);
                continue;
            }
            if (RecordReader.Parse(blob.Value, record.Path).Parsed is { } parsed)
                parses[canonical] = (parsed, record.Sha);
        }

        // 5. Each id's target (condition 3) and whether it is free here (condition 5; a check that throws is not free in a
        // proven pull and leaves the id uncleared in an unproven one). An id with an unread candidate lower than its target
        // (or with no target) has an unknown target and is uncleared.
        var free = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var parsing = SetModule.OfSeq(parses.Keys);
        var choices = new List<SyncAlias.Candidate>();
        foreach (var x in q)
        {
            var list = candidates[x];
            if (list.Count == 0 || uncleared.Contains(x))
                continue;
            var target = SyncAlias.target(ListModule.OfSeq(list.Select(c => c.Canonical)), parsing);
            if (list.Any(c => unread.Contains(c.Canonical) && (target is null || string.CompareOrdinal(c.Canonical, target.Value) < 0)))
            {
                uncleared.Add(x);
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
                    uncleared.Add(x);
                    continue;
                }
                isFree = known == true;
            }
            choices.Add(new SyncAlias.Candidate(x, target, isFree));
        }

        // 6. At most one member per canonical id converges (condition 6); the others are ignored. With an uncleared id (an
        // unproven pull only) nothing converges: an uncleared id may be the member that should, so the would-be converging
        // ids are ignored for this pull, as every id was before; their alias evidence keeps them excluded on the next pull.
        var choice = SyncAlias.choose(ListModule.OfSeq(choices));
        if (uncleared.Count > 0)
            return new AliasPass(
                new Dictionary<string, ConvergeTarget>(StringComparer.Ordinal),
                choice.Ignored.Concat(choice.Converging.Keys).ToHashSet(StringComparer.Ordinal));
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

        // Whether a memory or knowledge entry exists here under the id (true: not a W1 candidate; false: one); null when that
        // cannot be known (the local read throws, is unreadable or in two tiers, or the knowledge check throws).
        bool? EntryHere(string id)
        {
            try
            {
                return ReadLocal(id) switch
                {
                    LocalRead.Present => true,
                    LocalRead.Absent => InKnowledge(id) ? true : false,
                    _ => null,
                };
            }
            catch (Exception ex) when (IsMappingFailure(ex))
            {
                return null;
            }
        }
    }
}
