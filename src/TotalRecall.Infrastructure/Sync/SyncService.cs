using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
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
    private const string WatermarkKey = "cortex_last_pull_at";

    /// <summary>Memory tiers to search when looking up entries locally.</summary>
    private static readonly Tier[] AllMemoryTiers = { Tier.Hot, Tier.Warm, Tier.Cold };

    public SyncService(IStore localStore, IRemoteBackend remote, SyncQueue syncQueue, MsSqliteConnection conn)
    {
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _syncQueue = syncQueue ?? throw new ArgumentNullException(nameof(syncQueue));
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
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
    public async Task FlushAsync(CancellationToken ct)
    {
        var items = _syncQueue.Drain(50);
        if (items.Count == 0)
            return;

        // Group by entity type and operation
        var memoryUpserts = items.Where(i => i.EntityType == "memory" && i.Operation == "upsert").ToList();
        var memoryDeletes = items.Where(i => i.EntityType == "memory" && i.Operation == "delete").ToList();
        var usageItems = items.Where(i => i.EntityType == "usage").ToList();
        var retrievalItems = items.Where(i => i.EntityType == "retrieval").ToList();
        var compactionItems = items.Where(i => i.EntityType == "compaction").ToList();

        // Memory upserts — batch
        if (memoryUpserts.Count > 0)
        {
            try
            {
                var entries = memoryUpserts.Select(i =>
                {
                    var doc = JsonDocument.Parse(i.Payload);
                    var root = doc.RootElement;
                    return new SyncEntry(
                        Id: root.GetProperty("id").GetString()!,
                        Content: root.GetProperty("content").GetString()!,
                        EntryType: root.TryGetProperty("entry_type", out var et) && et.ValueKind == JsonValueKind.String
                            ? et.GetString()!
                            : "Preference",
                        ContentType: root.TryGetProperty("content_type", out var ct) && ct.ValueKind == JsonValueKind.String
                            ? ct.GetString()!
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
                }).ToArray();

                await _remote.UpsertMemoriesAsync(entries, ct).ConfigureAwait(false);
                foreach (var item in memoryUpserts)
                    _syncQueue.MarkCompleted(item.Id);
            }
            catch (CortexUnreachableException ex)
            {
                foreach (var item in memoryUpserts)
                    _syncQueue.MarkFailed(item.Id, ex.Message);
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

        // Usage telemetry
        if (usageItems.Count > 0)
        {
            try
            {
                var events = usageItems
                    .SelectMany(i => JsonSerializer.Deserialize(i.Payload, SyncJsonContext.Default.SyncUsageEventArray) ?? Array.Empty<SyncUsageEvent>())
                    .ToArray();
                await _remote.PushUsageEventsAsync(events, ct).ConfigureAwait(false);
                foreach (var item in usageItems)
                    _syncQueue.MarkCompleted(item.Id);
            }
            catch (CortexUnreachableException ex)
            {
                foreach (var item in usageItems)
                    _syncQueue.MarkFailed(item.Id, ex.Message);
            }
        }

        // Retrieval telemetry
        if (retrievalItems.Count > 0)
        {
            try
            {
                var events = retrievalItems
                    .SelectMany(i => JsonSerializer.Deserialize(i.Payload, SyncJsonContext.Default.SyncRetrievalEventArray) ?? Array.Empty<SyncRetrievalEvent>())
                    .ToArray();
                await _remote.PushRetrievalEventsAsync(events, ct).ConfigureAwait(false);
                foreach (var item in retrievalItems)
                    _syncQueue.MarkCompleted(item.Id);
            }
            catch (CortexUnreachableException ex)
            {
                foreach (var item in retrievalItems)
                    _syncQueue.MarkFailed(item.Id, ex.Message);
            }
        }

        // Compaction telemetry
        if (compactionItems.Count > 0)
        {
            try
            {
                var events = compactionItems
                    .SelectMany(i => JsonSerializer.Deserialize(i.Payload, SyncJsonContext.Default.SyncCompactionEntryArray) ?? Array.Empty<SyncCompactionEntry>())
                    .ToArray();
                await _remote.PushCompactionEntriesAsync(events, ct).ConfigureAwait(false);
                foreach (var item in compactionItems)
                    _syncQueue.MarkCompleted(item.Id);
            }
            catch (CortexUnreachableException ex)
            {
                foreach (var item in compactionItems)
                    _syncQueue.MarkFailed(item.Id, ex.Message);
            }
        }
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
