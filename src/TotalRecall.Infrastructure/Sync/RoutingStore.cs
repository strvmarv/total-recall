using System;
using System.Collections.Generic;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// Local-first <see cref="IStore"/> decorator that delegates every operation
/// to a local store and enqueues write operations to a <see cref="SyncQueue"/>
/// for eventual push to the remote backend. Reads never touch the remote.
/// </summary>
public sealed class RoutingStore : IStore
{
    private readonly IStore _local;
    private readonly IRemoteBackend _remote;
    private readonly SyncQueue _syncQueue;

    public RoutingStore(IStore local, IRemoteBackend remote, SyncQueue syncQueue)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _syncQueue = syncQueue ?? throw new ArgumentNullException(nameof(syncQueue));
    }

    /// <summary>Exposed so tool handlers can query the remote backend directly.</summary>
    public IRemoteBackend Remote => _remote;

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts)
    {
        var id = _local.Insert(tier, type, opts);
        EnqueueUpsert(tier, type, id);
        return id;
    }

    public string InsertWithEmbedding(
        Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding)
    {
        var id = _local.InsertWithEmbedding(tier, type, opts, embedding);
        EnqueueUpsert(tier, type, id);
        return id;
    }

    public Entry? Get(Tier tier, ContentType type, string id)
        => _local.Get(tier, type, id);

    public long? GetInternalKey(Tier tier, ContentType type, string id)
        => _local.GetInternalKey(tier, type, id);

    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts)
    {
        _local.Update(tier, type, id, opts);
        EnqueueUpsert(tier, type, id);
    }

    public void Delete(Tier tier, ContentType type, string id)
    {
        _local.Delete(tier, type, id);
        _syncQueue.Enqueue("memory", "delete", id,
            SyncPayload.Delete(id));
    }

    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
        => _local.List(tier, type, opts);

    public int Count(Tier tier, ContentType type)
        => _local.Count(tier, type);

    public int CountKnowledgeCollections()
        => _local.CountKnowledgeCollections();

    public IReadOnlyList<Entry> ListByMetadata(
        Tier tier, ContentType type,
        IReadOnlyDictionary<string, string> metadataFilter,
        ListEntriesOpts? opts = null)
        => _local.ListByMetadata(tier, type, metadataFilter, opts);

    public void Move(
        Tier fromTier, ContentType fromType,
        Tier toTier, ContentType toType,
        string id)
    {
        _local.Move(fromTier, fromType, toTier, toType, id);
        EnqueueUpsert(toTier, toType, id);
    }

    private void EnqueueUpsert(Tier tier, ContentType type, string id)
    {
        // Best-effort: the caller's local write has already committed by the
        // time we reach this method. A failure to enqueue the cortex-sync
        // payload must NOT surface as a user-visible write error — the
        // memory IS stored locally; only its eventual push to cortex is at
        // risk. Log to stderr so the drift is diagnosable. The next
        // successful write for this user will produce a fresh queue entry,
        // and PeriodicSync will still push unrelated queued items on its
        // timer.
        try
        {
            var entry = _local.Get(tier, type, id);
            if (entry is null) return; // defensive: shouldn't happen right after write
            _syncQueue.Enqueue("memory", "upsert", id, SyncPayload.Upsert(entry, type, tier));
        }
        catch (Exception ex)
        {
            // AOT-safe: format tier/type via TierNames helpers. String-
            // interpolating the raw F# DU values triggers F# reflection
            // (StructuredPrintfImpl) which fails under AOT trimming with
            // a KeyNotFoundException of its own — defeating the whole
            // "best-effort log and continue" contract.
            var tierName = TierNames.TierName(tier);
            var typeName = TierNames.ContentTypeName(type);
            Console.Error.WriteLine(
                "[total-recall] EnqueueUpsert failed for tier=" + tierName +
                " type=" + typeName + " id=" + id + ": " +
                ex.GetType().Name + ": " + ex.Message);
            if (ex.StackTrace is not null)
                Console.Error.WriteLine(ex.StackTrace);
        }
    }
}
