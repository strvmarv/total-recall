namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// Defines the plugin's view of the Cortex API for bi-directional sync.
/// Implementations translate between the local memory store and the remote
/// Cortex service, bridging F# core types and Cortex C# enums as strings
/// over the HTTP/JSON contract.
/// </summary>
public interface IRemoteBackend
{
    /// <summary>Search the remote knowledge base, optionally restricted to the given scopes.</summary>
    Task<SyncSearchResult[]> SearchKnowledgeAsync(string query, int topK, IReadOnlyList<string>? scopes, CancellationToken ct);

    /// <summary>Search remote memories within a given scope.</summary>
    Task<SyncSearchResult[]> SearchMemoriesAsync(string query, string scope, int topK, CancellationToken ct);

    /// <summary>Retrieve tier-bucketed memory counts and total size from the remote.</summary>
    Task<SyncStatusResult> GetStatusAsync(CancellationToken ct);

    /// <summary>Push new or updated memory entries to the remote.</summary>
    Task UpsertMemoriesAsync(SyncEntry[] entries, CancellationToken ct);

    /// <summary>Tombstone a single memory on the remote.</summary>
    Task DeleteMemoryAsync(string id, CancellationToken ct);

    /// <summary>Pull all memories modified after the given watermark.</summary>
    Task<SyncPullResult> GetUserMemoriesModifiedSinceAsync(DateTimeOffset since, CancellationToken ct);

    /// <summary>Push usage telemetry events to the remote.</summary>
    Task PushUsageEventsAsync(SyncUsageEvent[] events, CancellationToken ct);

    /// <summary>Push retrieval telemetry events to the remote.</summary>
    Task PushRetrievalEventsAsync(SyncRetrievalEvent[] events, CancellationToken ct);

    /// <summary>Push compaction log entries to the remote.</summary>
    Task PushCompactionEntriesAsync(SyncCompactionEntry[] entries, CancellationToken ct);
}

/// <summary>
/// Thrown when the Cortex remote backend cannot be reached (network error,
/// DNS failure, timeout, etc.).
/// </summary>
public class CortexUnreachableException : Exception
{
    public CortexUnreachableException(string message, Exception? inner = null)
        : base(message, inner) { }
}
