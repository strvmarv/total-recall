// src/TotalRecall.Infrastructure/Eval/SessionConfigSnapshot.cs
//
// The id of the config snapshot a server session runs with. One instance is
// created per server composition and shared by SessionLifecycle and the
// handlers that log retrieval / compaction events, so every event of the
// session carries the same ConfigSnapshotId.
//
// Resolution is lazy: whoever asks first (session_start, or an event logged
// before session_start) creates or reuses the snapshot through
// IConfigSnapshotStore.CreateSnapshot, which reuses the latest row when the
// config JSON is unchanged. The id is cached for the session. A store failure
// returns null and is not cached, so the next call retries.

using System;

namespace TotalRecall.Infrastructure.Eval;

public sealed class SessionConfigSnapshot
{
    private readonly IConfigSnapshotStore _store;
    private readonly string _configJson;
    private readonly Action<string> _onError;
    private readonly object _gate = new();
    private string? _id;

    /// <param name="store">Snapshot store on the shared connection.</param>
    /// <param name="configJson">
    /// The serialized config the server runs with (the in-process config, not
    /// a re-read of the file).
    /// </param>
    /// <param name="onError">Receives one line per failed resolve; defaults to stderr.</param>
    public SessionConfigSnapshot(IConfigSnapshotStore store, string configJson, Action<string>? onError = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _configJson = configJson ?? throw new ArgumentNullException(nameof(configJson));
        _onError = onError ?? (line => Console.Error.WriteLine(line));
    }

    /// <summary>
    /// The session's snapshot id, creating or reusing the snapshot on first
    /// use. Returns null when the store fails; the failure is not cached.
    /// </summary>
    public string? Resolve()
    {
        lock (_gate)
        {
            if (_id is not null) return _id;
            try
            {
                _id = _store.CreateSnapshot(_configJson);
                return _id;
            }
            catch (Exception ex)
            {
                _onError($"[total-recall] config snapshot unavailable; events keep the placeholder id: {ex.Message}");
                return null;
            }
        }
    }
}
