using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// Orchestrates the sync lifecycle: pull from Cortex, flush queue to Cortex,
/// push telemetry. Gracefully degrades when Cortex is unreachable.
/// </summary>
public sealed class SyncService
{
    private readonly IStore _localStore;
    private readonly IRemoteBackend _remote;
    private readonly SyncQueue _syncQueue;
    private readonly MsSqliteConnection _conn;
    private readonly ISkillCache? _skillCache;
    private const string WatermarkKey = "cortex_last_pull_at";
    private const string SkillsWatermarkKey = "cortex_skills_last_pull_at";

    // NOTE: pinned is intentionally EXCLUDED. The pinned tier is local-only
    // (Cortex has no pinned support yet), so the remote never holds pinned
    // entries to reconcile. Including pinned here would let remote tombstones/
    // updates mutate or delete a local pin, which is wrong. (User decision 2026-06-09.)
    private static readonly Tier[] AllMemoryTiers = { Tier.Hot, Tier.Warm, Tier.Cold };

    public SyncService(IStore localStore, IRemoteBackend remote, SyncQueue syncQueue, MsSqliteConnection conn, ISkillCache? skillCache = null)
    {
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _syncQueue = syncQueue ?? throw new ArgumentNullException(nameof(syncQueue));
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        _skillCache = skillCache;
    }

    /// <summary>
    /// Pull remote memories modified since the last watermark and reconcile
    /// with the local store. Silently returns if Cortex is unreachable.
    /// </summary>
    public async Task PullAsync(CancellationToken ct)
    {
        var watermark = GetWatermark(WatermarkKey);

        SyncPullResult result;
        try
        {
            result = await _remote.GetUserMemoriesModifiedSinceAsync(watermark, ct).ConfigureAwait(false);
        }
        catch (CortexUnreachableException)
        {
            return; // graceful degradation
        }

        foreach (var entry in result.Memories)
        {
            if (entry.DeletedAt != null)
            {
                // Tombstone: find locally across all memory tiers and delete
                foreach (var tier in AllMemoryTiers)
                {
                    var local = _localStore.Get(tier, ContentType.Memory, entry.Id);
                    if (local != null)
                    {
                        _localStore.Delete(tier, ContentType.Memory, entry.Id);
                        break;
                    }
                }
            }
            else
            {
                // Find locally across all tiers
                Entry? localEntry = null;
                Tier? localTier = null;
                foreach (var tier in AllMemoryTiers)
                {
                    localEntry = _localStore.Get(tier, ContentType.Memory, entry.Id);
                    if (localEntry != null)
                    {
                        localTier = tier;
                        break;
                    }
                }

                if (localEntry == null)
                {
                    var pullTier = entry.Tier is not null
                        ? TierNames.ParseTier(entry.Tier) ?? Tier.Hot
                        : Tier.Hot;
                    var opts = new InsertEntryOpts(
                        Content: entry.Content,
                        Source: entry.Source,
                        Tags: entry.Tags,
                        Id: entry.Id,
                        Scope: entry.Scope,
                        EntryType: EntryType.Imported);
                    _localStore.Insert(pullTier, ContentType.Memory, opts);
                }
                else
                {
                    // Compare timestamps: remote UpdatedAt is DateTime (epoch ms in local store)
                    var remoteUpdatedMs = new DateTimeOffset(entry.UpdatedAt, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    if (remoteUpdatedMs > localEntry.UpdatedAt)
                    {
                        _localStore.Update(localTier!, ContentType.Memory, entry.Id, new UpdateEntryOpts
                        {
                            Content = entry.Content,
                            Tags = entry.Tags
                        });
                    }
                    // else: local is newer, skip
                }
            }
        }

        SetWatermark(WatermarkKey, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Drain the sync queue and push entries to the remote backend. On
    /// <see cref="CortexUnreachableException"/>, items are marked as failed
    /// (not removed from the queue).
    /// </summary>
    // Per-type Phase 1 drain quotas. Each type gets a guaranteed slot per flush
    // so a backlog of one type can't starve the others (see Bug A regression test).
    // These are an anti-starvation floor, NOT a per-flush cap — a Phase 2
    // catch-up below keeps draining each telemetry type until empty so a
    // backlog isn't bounded to {Limit} rows per flush.
    private const int MemoryDrainLimit = 25;
    private const int UsageDrainLimit = 10;
    private const int RetrievalDrainLimit = 10;
    private const int CompactionDrainLimit = 5;

    // Phase 2 catch-up batch parameters. Larger batches than Phase 1 because
    // telemetry payloads are small and the goal is to clear backlogs quickly.
    // MaxCatchUpBatches is a runaway-protection ceiling.
    private const int CatchUpBatchSize = 100;
    private const int MaxCatchUpBatches = 100;

    public async Task FlushAsync(CancellationToken ct)
    {
        var memoryItems = _syncQueue.Drain("memory", MemoryDrainLimit);
        var memoryUpserts = memoryItems.Where(i => i.Operation == "upsert").ToList();
        var memoryDeletes = memoryItems.Where(i => i.Operation == "delete").ToList();
        var usageItems = _syncQueue.Drain("usage", UsageDrainLimit);
        var retrievalItems = _syncQueue.Drain("retrieval", RetrievalDrainLimit);
        var compactionItems = _syncQueue.Drain("compaction", CompactionDrainLimit);

        // Memory upserts — batch. Parse defensively: a payload that can't be
        // parsed is quarantined (MarkFailed) rather than aborting the flush, so
        // one corrupt row can't crash session_end or block the rest of the queue.
        if (memoryUpserts.Count > 0)
        {
            var parsed = ParseOrQuarantine(memoryUpserts, ParseSyncEntry);
            if (parsed.Count > 0)
            {
                try
                {
                    await _remote.UpsertMemoriesAsync(
                        parsed.Select(p => p.Parsed).ToArray(), ct).ConfigureAwait(false);
                    foreach (var (item, _) in parsed)
                        _syncQueue.MarkCompleted(item.Id);
                }
                catch (CortexUnreachableException ex)
                {
                    foreach (var (item, _) in parsed)
                        _syncQueue.MarkFailed(item.Id, ex.Message);
                }
            }
        }

        // Memory deletes — individual
        foreach (var item in memoryDeletes)
        {
            try
            {
                await _remote.DeleteMemoryAsync(item.EntityId!, ct).ConfigureAwait(false);
                _syncQueue.MarkCompleted(item.Id);
            }
            catch (CortexUnreachableException ex)
            {
                _syncQueue.MarkFailed(item.Id, ex.Message);
            }
        }

        // Telemetry (usage / retrieval / compaction). Each batch is parsed
        // defensively and pushed; un-parseable rows are quarantined per-item.
        await FlushTelemetryBatchAsync(usageItems, ParseUsage,
            events => _remote.PushUsageEventsAsync(events, ct), ct).ConfigureAwait(false);
        await FlushTelemetryBatchAsync(retrievalItems, ParseRetrieval,
            events => _remote.PushRetrievalEventsAsync(events, ct), ct).ConfigureAwait(false);
        await FlushTelemetryBatchAsync(compactionItems, ParseCompaction,
            events => _remote.PushCompactionEntriesAsync(events, ct), ct).ConfigureAwait(false);

        // Phase 2: catch-up drain for the three telemetry queues. Phase 1's
        // small fair-share quotas protected against starvation; this loop
        // ensures a backlog isn't capped at {Limit} rows per flush.
        //
        // Memory upserts/deletes intentionally not in catch-up — payloads are
        // larger and per-flush bounding is acceptable until a memory backlog
        // becomes a real problem.
        if (!await CatchUpTelemetryAsync("usage", ParseUsage,
                events => _remote.PushUsageEventsAsync(events, ct), ct).ConfigureAwait(false))
            return;
        if (!await CatchUpTelemetryAsync("retrieval", ParseRetrieval,
                events => _remote.PushRetrievalEventsAsync(events, ct), ct).ConfigureAwait(false))
            return;
        if (!await CatchUpTelemetryAsync("compaction", ParseCompaction,
                events => _remote.PushCompactionEntriesAsync(events, ct), ct).ConfigureAwait(false))
            return;

        // Drain unsynced skill_usage_events in chunks of 100. Each chunk is pushed
        // as a single batch; failure stops the loop and leaves remaining rows queued.
        while (true)
        {
            var batch = ReadUnsyncedSkillUsage(limit: 100);
            if (batch.Count == 0) break;
            try
            {
                await _remote.PushSkillUsageAsync(batch.Select(b => b.Event).ToArray(), ct).ConfigureAwait(false);
                MarkSkillUsageSynced(batch.Select(b => b.Id).ToList());
            }
            catch (CortexUnreachableException)
            {
                return;
            }
        }
    }

    // Phase 2 catch-up helper. Returns true on normal completion (empty queue,
    // all rows quarantined, or hit safety cap), false if CortexUnreachableException
    // terminated the loop — caller bails on false so we don't keep hammering a
    // dead remote. Quarantined (un-parseable) rows are marked failed and excluded
    // from the next drain by their backoff window, so the loop still terminates.
    private async Task<bool> CatchUpTelemetryAsync<T>(
        string entityType,
        Func<string, T[]> parse,
        Func<T[], Task> push,
        CancellationToken ct)
    {
        for (int i = 0; i < MaxCatchUpBatches; i++)
        {
            var batch = _syncQueue.Drain(entityType, CatchUpBatchSize);
            if (batch.Count == 0) return true;
            if (!await FlushTelemetryBatchAsync(batch, parse, push, ct).ConfigureAwait(false))
                return false; // remote unreachable — stop catch-up
        }
        return true;
    }

    // Parse a drained telemetry batch defensively and push the parsed events.
    // Returns false ONLY when the remote is unreachable (transient — caller may
    // stop). Un-parseable rows are quarantined per-item and do not fail the batch.
    private async Task<bool> FlushTelemetryBatchAsync<T>(
        IReadOnlyList<SyncQueueItem> items,
        Func<string, T[]> parse,
        Func<T[], Task> push,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (items.Count == 0) return true;

        var parsed = ParseOrQuarantine(items, parse);
        if (parsed.Count == 0) return true; // every row quarantined — nothing to push

        var events = parsed.SelectMany(p => p.Parsed).ToArray();
        try
        {
            await push(events).ConfigureAwait(false);
            foreach (var (item, _) in parsed)
                _syncQueue.MarkCompleted(item.Id);
            return true;
        }
        catch (CortexUnreachableException ex)
        {
            foreach (var (item, _) in parsed)
                _syncQueue.MarkFailed(item.Id, ex.Message);
            return false;
        }
    }

    // Parse each drained item's payload, routing any item whose payload is
    // permanently un-parseable (malformed JSON, missing/wrong-typed fields) to
    // MarkFailed — it can never succeed, so it is quarantined with an error and a
    // backoff window rather than aborting the flush or wedging the whole queue.
    // Transport errors (CortexUnreachableException) are NOT raised here — parsing
    // never touches the remote — so they cannot be misclassified as poison.
    private List<(SyncQueueItem Item, T Parsed)> ParseOrQuarantine<T>(
        IReadOnlyList<SyncQueueItem> items, Func<string, T> parse)
    {
        var ok = new List<(SyncQueueItem, T)>(items.Count);
        foreach (var item in items)
        {
            T parsed;
            try
            {
                parsed = parse(item.Payload);
            }
            catch (Exception ex) when (
                ex is JsonException or KeyNotFoundException or InvalidOperationException
                   or FormatException or OverflowException or NotSupportedException)
            {
                _syncQueue.MarkFailed(item.Id, "unparseable payload (quarantined): " + ex.Message);
                // Surface the quarantine: otherwise a permanently-stuck row keeps
                // PendingCount() non-zero with no visible reason. last_error holds
                // the detail; this makes the event observable in the process log.
                Console.Error.WriteLine(
                    $"[total-recall] sync item {item.Id} ({item.EntityType}) quarantined: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            ok.Add((item, parsed));
        }
        return ok;
    }

    // A literal "null" payload deserializes to null. That is never something
    // SyncPayload emits (telemetry is always a JSON array), so treat it as
    // corruption and let ParseOrQuarantine isolate the row rather than silently
    // dropping it via a coalesce-to-empty. A genuinely empty batch is "[]",
    // which deserializes to a zero-length array, not null.
    private static SyncUsageEvent[] ParseUsage(string payload)
        => JsonSerializer.Deserialize(payload, SyncJsonContext.Default.SyncUsageEventArray)
           ?? throw new JsonException("usage payload deserialized to null");

    private static SyncRetrievalEvent[] ParseRetrieval(string payload)
        => JsonSerializer.Deserialize(payload, SyncJsonContext.Default.SyncRetrievalEventArray)
           ?? throw new JsonException("retrieval payload deserialized to null");

    private static SyncCompactionEntry[] ParseCompaction(string payload)
        => JsonSerializer.Deserialize(payload, SyncJsonContext.Default.SyncCompactionEntryArray)
           ?? throw new JsonException("compaction payload deserialized to null");

    // Hydrate a SyncEntry from a queued memory-upsert payload. The JsonDocument is
    // disposed before return — every field is copied out to a managed value/string.
    private static SyncEntry ParseSyncEntry(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        return new SyncEntry(
            Id: root.GetProperty("id").GetString()!,
            Content: root.GetProperty("content").GetString()!,
            EntryType: root.TryGetProperty("entry_type", out var et) && et.ValueKind == JsonValueKind.String
                ? et.GetString()!
                : "Preference",
            ContentType: root.TryGetProperty("content_type", out var ctp) && ctp.ValueKind == JsonValueKind.String
                ? ctp.GetString()!
                : "Memory",
            Tags: root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array
                ? tagsEl.EnumerateArray().Select(t => t.GetString()!).ToArray()
                : Array.Empty<string>(),
            Source: root.TryGetProperty("source", out var srcEl) && srcEl.ValueKind == JsonValueKind.String
                ? srcEl.GetString()
                : null,
            AccessCount: root.TryGetProperty("access_count", out var acEl) && acEl.ValueKind == JsonValueKind.Number
                ? acEl.GetInt32()
                : 0,
            DecayScore: root.TryGetProperty("decay_score", out var dsEl) && dsEl.ValueKind == JsonValueKind.Number
                ? dsEl.GetDouble()
                : 1.0,
            CreatedAt: root.TryGetProperty("created_at", out var caEl) && caEl.ValueKind == JsonValueKind.String
                ? caEl.GetDateTime()
                : DateTime.UtcNow,
            UpdatedAt: root.TryGetProperty("updated_at", out var uaEl) && uaEl.ValueKind == JsonValueKind.String
                ? uaEl.GetDateTime()
                : DateTime.UtcNow,
            Scope: root.TryGetProperty("scope", out var scEl) && scEl.ValueKind == JsonValueKind.String
                ? scEl.GetString()
                : null,
            Tier: root.TryGetProperty("tier", out var tierEl) && tierEl.ValueKind == JsonValueKind.String
                ? tierEl.GetString()
                : null);
    }

    /// <summary>
    /// Pull skills modified since the last watermark and reconcile with the
    /// local skill cache. Silently returns if Cortex is unreachable or if
    /// no skill cache is configured.
    /// </summary>
    public async Task PullSkillsAsync(CancellationToken ct)
    {
        if (_skillCache is null)
            return;

        var watermark = GetWatermark(SkillsWatermarkKey);
        DateTime? since = watermark == DateTimeOffset.MinValue ? null : watermark.UtcDateTime;

        PluginSyncSkillDto[] skills;
        try
        {
            skills = await _remote.GetSkillsModifiedSinceAsync(since, ct).ConfigureAwait(false);
        }
        catch (CortexUnreachableException)
        {
            return; // graceful degradation
        }

        foreach (var skill in skills)
        {
            if (skill.IsOrphaned)
                await _skillCache.RemoveAsync(skill.Id, ct).ConfigureAwait(false);
            else
                await _skillCache.UpsertAsync(skill, ct).ConfigureAwait(false);
        }

        SetWatermark(SkillsWatermarkKey, DateTimeOffset.UtcNow);
    }

    // --- Skill usage helpers -------------------------------------------------

    private List<(string Id, PluginSyncSkillUsageEvent Event)> ReadUnsyncedSkillUsage(int limit)
    {
        var rows = new List<(string, PluginSyncSkillUsageEvent)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, skill_id, occurred_at, host, session_id
              FROM skill_usage_events
             WHERE synced_at IS NULL
             ORDER BY occurred_at
             LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add((
                r.GetString(0),
                new PluginSyncSkillUsageEvent(
                    SkillId: Guid.Parse(r.GetString(1)),
                    OccurredAt: DateTime.Parse(r.GetString(2)).ToUniversalTime(),
                    Host: r.IsDBNull(3) ? null : r.GetString(3),
                    SessionId: r.IsDBNull(4) ? null : r.GetString(4))));
        }
        return rows;
    }

    private void MarkSkillUsageSynced(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE skill_usage_events SET synced_at = $now WHERE id = $id";
        var pn = cmd.Parameters.Add("$now", Microsoft.Data.Sqlite.SqliteType.Text);
        var pi = cmd.Parameters.Add("$id",  Microsoft.Data.Sqlite.SqliteType.Text);
        pn.Value = DateTime.UtcNow.ToString("O");
        foreach (var id in ids)
        {
            pi.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // --- Watermark helpers ---------------------------------------------------

    internal DateTimeOffset GetWatermark(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var result = cmd.ExecuteScalar();
        if (result is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto;
        return DateTimeOffset.MinValue;
    }

    internal void SetWatermark(string key, DateTimeOffset value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO _meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
