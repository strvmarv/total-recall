using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncScanner
{
    /// <summary>
    /// The secret pass (#161, decisions D3 and D4), run on the plan after the main loop and before the write transaction,
    /// so the detectors never run inside a transaction. For every eligible entry it takes the row the plan will write
    /// (the plan's own row, else the stored row when it is <c>active</c>), decides with <see cref="SyncSecret"/> whether
    /// to scan, and applies the mark change to that row: one row per id, replacing the plan's row or adding the stored one
    /// when it changed. An entry the main loop calls unchanged is therefore still checked when its key matches neither
    /// stored key. Ids whose secret mark appears go to <paramref name="blocked"/>, ids whose mark disappears to
    /// <paramref name="unblocked"/>. <c>allowed_hash</c> is never written here.
    /// </summary>
    private void ApplySecretPass(Snapshot snap, ScanPlan plan, List<string> blocked, List<string> unblocked)
    {
        var index = new Dictionary<string, int>(plan.Rows.Count, StringComparer.Ordinal);
        for (var i = 0; i < plan.Rows.Count; i++)
            index[plan.Rows[i].EntryId] = i;

        foreach (var (id, entry) in snap.Entries)
        {
            if (snap.SkippedIds.Contains(id))
                continue;
            var planned = index.TryGetValue(id, out var at);
            GitSyncRow? held = planned ? plan.Rows[at] : snap.Rows.GetValueOrDefault(id);
            // No row (a fresh row the clock could not stamp) or a forgotten/left row the plan does not reactivate.
            if (held is null || !held.State.IsActive)
                continue;

            var view = entry.View;
            var key = SyncSecret.secretKey(view.Content, view.Tags);
            var reason = Opt(held.BlockedReason);
            var check = SyncSecret.precheck(reason, Opt(held.BlockedHash), Opt(held.AllowedHash), key);
            var change =
                check.IsAllowed ? SyncSecret.afterAllowed(reason)
                : check.IsStillBlocked ? SyncSecret.MarkChange.KeepMark
                : SyncSecret.afterScan(reason, SecretFields.AnyHit(view, SecretDetector), key);

            var next = change switch
            {
                SyncSecret.MarkChange.SetBlock set => held with { BlockedReason = SyncSecret.blockReason, BlockedHash = set.key },
                _ when change.IsClearBlock => held with { BlockedReason = null, BlockedHash = null },
                _ => held,
            };
            if (next.Equals(held))
                continue;

            if (planned)
                plan.Rows[at] = next;
            else
                plan.Rows.Add(next);

            // a block the scan set on a changed row is listed even when the row was blocked before (a new key replaced the old)
            if (change.IsSetBlock)
                blocked.Add(id);
            else if (held.BlockedReason == SyncSecret.blockReason && next.BlockedReason != SyncSecret.blockReason)
                unblocked.Add(id);
        }
    }
}
