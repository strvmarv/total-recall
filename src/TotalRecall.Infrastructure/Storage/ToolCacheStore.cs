using System;
using System.Security.Cryptography;
using System.Text;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>A fresh cache row returned by <see cref="ToolCacheStore.Check"/>.</summary>
public sealed record CacheCheckHit(string Content, long StoredAtMs, int TokenEstimate);

/// <summary>
/// Phase 3 idea 2c — tool-result cache over the <c>tool_cache</c> table.
/// Borrows a non-owning <see cref="MsSqliteConnection"/> (same pattern as
/// <see cref="TotalRecall.Infrastructure.Telemetry.RetrievalEventLog"/>).
///
/// Freshness: a row is a hit iff ageSeconds &lt;= ttl_seconds AND
/// (maxAgeSeconds is null OR ageSeconds &lt;= maxAgeSeconds). Expired rows
/// are lazily deleted on Check and bulk-purged on every StoreResult.
/// Capacity: LRU eviction (by COALESCE(last_hit_at_ms, stored_at_ms))
/// down to <c>maxEntries</c> on every StoreResult.
///
/// Session counters (hits/misses/tokensSaved) are in-memory only and feed
/// the session_refresh efficiency block. Cortex sync of cache telemetry is
/// deferred (spec §7.1.2 note).
/// </summary>
public sealed class ToolCacheStore
{
    private readonly MsSqliteConnection _conn;
    private readonly int _maxEntries;
    private readonly int _defaultTtlSeconds;
    private readonly Func<long> _nowMs;
    private readonly object _statsLock = new();
    private long _sessionHits;
    private long _sessionMisses;
    private long _sessionTokensSaved;

    public ToolCacheStore(
        MsSqliteConnection conn,
        int maxEntries = 200,
        int defaultTtlSeconds = 600,
        Func<long>? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        _conn = conn;
        _maxEntries = maxEntries > 0 ? maxEntries : 200;
        _defaultTtlSeconds = defaultTtlSeconds > 0 ? defaultTtlSeconds : 600;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Look up a cached result. Returns the hit (and bumps hit_count /
    /// last_hit_at_ms / session counters) when fresh; returns null on miss
    /// or staleness. Rows past their own TTL are deleted on the way out.
    /// </summary>
    public CacheCheckHit? Check(string tool, string argsHash, int? maxAgeSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsHash);

        string? content = null;
        long storedAtMs = 0;
        long ttlSeconds = 0;
        int tokenEstimate = 0;

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT content, stored_at_ms, ttl_seconds, token_estimate " +
                "FROM tool_cache WHERE tool = $tool AND args_hash = $hash";
            cmd.Parameters.AddWithValue("$tool", tool);
            cmd.Parameters.AddWithValue("$hash", argsHash);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                content = reader.GetString(0);
                storedAtMs = reader.GetInt64(1);
                ttlSeconds = reader.GetInt64(2);
                tokenEstimate = (int)reader.GetInt64(3);
            }
        }

        if (content is null)
        {
            RecordMiss();
            return null;
        }

        var now = _nowMs();
        var ageSeconds = (now - storedAtMs) / 1000;
        var fresh = ageSeconds <= ttlSeconds
            && (maxAgeSeconds is null || ageSeconds <= maxAgeSeconds.Value);

        if (!fresh)
        {
            // Past its own ttl → dead weight, delete. (A row that merely
            // fails the caller's narrower maxAge stays for wider callers.)
            if (ageSeconds > ttlSeconds)
            {
                using var del = _conn.CreateCommand();
                del.CommandText =
                    "DELETE FROM tool_cache WHERE tool = $tool AND args_hash = $hash";
                del.Parameters.AddWithValue("$tool", tool);
                del.Parameters.AddWithValue("$hash", argsHash);
                del.ExecuteNonQuery();
            }
            RecordMiss();
            return null;
        }

        using (var upd = _conn.CreateCommand())
        {
            upd.CommandText =
                "UPDATE tool_cache SET hit_count = hit_count + 1, last_hit_at_ms = $now " +
                "WHERE tool = $tool AND args_hash = $hash";
            upd.Parameters.AddWithValue("$now", now);
            upd.Parameters.AddWithValue("$tool", tool);
            upd.Parameters.AddWithValue("$hash", argsHash);
            upd.ExecuteNonQuery();
        }

        lock (_statsLock)
        {
            _sessionHits++;
            _sessionTokensSaved += tokenEstimate;
        }
        return new CacheCheckHit(content, storedAtMs, tokenEstimate);
    }

    /// <summary>
    /// Upsert a result row, then purge expired rows and enforce the LRU cap.
    /// Returns the heuristic token estimate stored with the row.
    /// </summary>
    public int StoreResult(string tool, string argsHash, string content, int? ttlSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsHash);
        ArgumentNullException.ThrowIfNull(content);

        var now = _nowMs();
        var ttl = ttlSeconds is > 0 ? ttlSeconds.Value : _defaultTtlSeconds;
        var tokenEstimate = EstimateTokens(content);
        var contentHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content)));

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tool_cache
                  (tool, args_hash, content, content_hash, stored_at_ms,
                   ttl_seconds, hit_count, last_hit_at_ms, token_estimate)
                VALUES ($tool, $hash, $content, $chash, $now, $ttl, 0, NULL, $tokens)
                ON CONFLICT(tool, args_hash) DO UPDATE SET
                  content = excluded.content,
                  content_hash = excluded.content_hash,
                  stored_at_ms = excluded.stored_at_ms,
                  ttl_seconds = excluded.ttl_seconds,
                  hit_count = 0,
                  last_hit_at_ms = NULL,
                  token_estimate = excluded.token_estimate
                """;
            cmd.Parameters.AddWithValue("$tool", tool);
            cmd.Parameters.AddWithValue("$hash", argsHash);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$chash", contentHash);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$ttl", ttl);
            cmd.Parameters.AddWithValue("$tokens", tokenEstimate);
            cmd.ExecuteNonQuery();
        }

        // Lazy purge of expired rows.
        using (var purge = _conn.CreateCommand())
        {
            purge.CommandText =
                "DELETE FROM tool_cache WHERE stored_at_ms + ttl_seconds * 1000 < $now";
            purge.Parameters.AddWithValue("$now", now);
            purge.ExecuteNonQuery();
        }

        // LRU cap: keep the maxEntries most-recently-used rows.
        using (var lru = _conn.CreateCommand())
        {
            lru.CommandText = """
                DELETE FROM tool_cache WHERE (tool, args_hash) IN (
                  SELECT tool, args_hash FROM tool_cache
                  ORDER BY COALESCE(last_hit_at_ms, stored_at_ms) DESC
                  LIMIT -1 OFFSET $max)
                """;
            lru.Parameters.AddWithValue("$max", _maxEntries);
            lru.ExecuteNonQuery();
        }

        return tokenEstimate;
    }

    /// <summary>Per-session counters for the session_refresh efficiency block.</summary>
    public (long Hits, long Misses, long TokensSaved) GetSessionStats()
    {
        lock (_statsLock)
        {
            return (_sessionHits, _sessionMisses, _sessionTokensSaved);
        }
    }

    private void RecordMiss()
    {
        lock (_statsLock) { _sessionMisses++; }
    }

    /// <summary>
    /// Word-count heuristic (words × 0.75). Duplicated from
    /// SessionLifecycle.HeuristicEstimateTokens because Infrastructure
    /// cannot reference Server; ±20% error is fine for savings reporting.
    /// </summary>
    internal static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 0.75);
    }
}
