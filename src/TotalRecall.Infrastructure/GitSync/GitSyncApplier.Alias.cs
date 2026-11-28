using Microsoft.FSharp.Collections;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// The target of a converging id (alias D3): the canonical id, its record as read at <c>R</c>, the record's blob id and the
/// group's origin hash.
/// </summary>
internal sealed record ConvergeTarget(string CanonicalId, ParsedRecord Record, string Sha, string Hash);

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
    /// and ignored ids. It fails closed: a blob read that is not ok (failed or cancelled), or a W1 candidate whose local read
    /// or knowledge check cannot be made, ignores every such local id for this pull, and a target that cannot be read is not
    /// free. Blobs are read through <paramref name="blobs"/>, so no blob is read twice. It never stops the pull itself.
    /// </summary>
    private async Task<AliasPass> PrepareAliases(
        IReadOnlyList<string> ids,
        Dictionary<string, string> skippedByScan,
        TreeView view,
        Dictionary<string, GitSyncRow> rows,
        Dictionary<string, HeldItem> held,
        Dictionary<string, GitOutcome<byte[]>> blobs,
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

        // Fail closed: every id of Q is kept for this pull and retried by the next.
        var ignoreAll = new AliasPass(
            new Dictionary<string, ConvergeTarget>(StringComparer.Ordinal), q.ToHashSet(StringComparer.Ordinal));

        async Task<GitOutcome<byte[]>> Blob(string id, AcceptedRecord record)
        {
            if (!blobs.TryGetValue(id, out var blob))
            {
                blob = await _client.CatBlobAsync(record.Sha, SyncValidation.maxFileBytes, ct).ConfigureAwait(false);
                blobs[id] = blob;
            }
            return blob;
        }

        // 3. The detections of this pull's W1 candidates (a detection counts like an open row, D2).
        var hashesOfQ = q.SelectMany(x => GitSyncAliases.HashesOf(conn, x)).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!view.Accepted.TryGetValue(id, out var record) || rows.ContainsKey(id) || skippedByScan.ContainsKey(id)
                || !SyncValidation.isEligibleId(id))
                continue;
            LocalRead read;
            try
            {
                read = ReadLocal(id);
            }
            catch (Exception ex) when (IsMappingFailure(ex))
            {
                return ignoreAll;
            }
            if (read is LocalRead.Present)
                continue;
            if (read is not LocalRead.Absent)
                return ignoreAll; // unreadable or in two tiers: the entry's own visit skips it, so it is no proof of anything
            bool knowledge;
            try
            {
                knowledge = InKnowledge(id);
            }
            catch (Exception ex) when (IsMappingFailure(ex))
            {
                return ignoreAll;
            }
            if (knowledge)
                continue;
            var blob = await Blob(id, record).ConfigureAwait(false);
            if (!blob.IsOk)
                return ignoreAll;
            if (RecordReader.Parse(blob.Value, record.Path).Parsed?.Record.Origin is not { } origin)
                continue; // a record that does not parse cannot be inserted either; one with no origin aliases nothing
            var hash = origin.Value.Hash;
            if (!hashesOfQ.Contains(hash) || GitSyncAliases.PlanSkip(conn, id, hash) is not { } detection)
                continue;
            foreach (var x in q)
                if (!string.Equals(x, detection.Canonical, StringComparison.Ordinal) && detection.Targets.Contains(x, StringComparer.Ordinal))
                    candidates[x].Add((detection.Canonical, hash));
        }

        // 4. The candidate canonical records accepted at R that parse. A read that is not ok is not "does not parse".
        var parses = new Dictionary<string, (ParsedRecord Record, string Sha)>(StringComparer.Ordinal);
        foreach (var canonical in candidates.Values.SelectMany(l => l.Select(c => c.Canonical)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!view.Accepted.TryGetValue(canonical, out var record))
                continue;
            var blob = await Blob(canonical, record).ConfigureAwait(false);
            if (!blob.IsOk)
                return ignoreAll;
            if (RecordReader.Parse(blob.Value, record.Path).Parsed is { } parsed)
                parses[canonical] = (parsed, record.Sha);
        }

        // 5. Each id's target (condition 3) and whether it is free here (condition 5; a read that fails is not free).
        var free = new Dictionary<string, bool>(StringComparer.Ordinal);
        var parsing = SetModule.OfSeq(parses.Keys);
        var choices = new List<SyncAlias.Candidate>();
        foreach (var x in q)
        {
            var list = candidates[x];
            if (list.Count == 0)
                continue;
            var target = SyncAlias.target(ListModule.OfSeq(list.Select(c => c.Canonical)), parsing);
            var isFree = false;
            if (target is not null)
            {
                var t = target.Value;
                if (!free.TryGetValue(t, out isFree))
                    free[t] = isFree = IsFree(t);
            }
            choices.Add(new SyncAlias.Candidate(x, target, isFree));
        }

        // 6. At most one member per canonical id converges (condition 6); the others are ignored.
        var choice = SyncAlias.choose(ListModule.OfSeq(choices));
        var converging = new Dictionary<string, ConvergeTarget>(StringComparer.Ordinal);
        foreach (var (x, t) in choice.Converging)
        {
            var (record, sha) = parses[t];
            var hash = candidates[x].First(c => string.Equals(c.Canonical, t, StringComparison.Ordinal)).Hash;
            converging[x] = new ConvergeTarget(t, record, sha, hash);
        }
        return new AliasPass(converging, choice.Ignored.ToHashSet(StringComparer.Ordinal));

        bool IsFree(string t)
        {
            try
            {
                return ReadLocal(t) is LocalRead.Absent && !InKnowledge(t) && !rows.ContainsKey(t) && !GitSyncReviews.HasAnyItem(conn, t);
            }
            catch (Exception ex) when (IsMappingFailure(ex))
            {
                return false;
            }
        }
    }
}
