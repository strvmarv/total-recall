using System;
using Npgsql;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Telemetry;

/// <summary>
/// Postgres-backed implementation of the import_log write surface. Mirrors
/// <see cref="ImportLog"/> but uses <see cref="NpgsqlDataSource"/> and
/// positional parameters ($1, $2, …) instead of a SQLite connection.
/// The <c>import_log</c> table is created by <c>PostgresMigrationRunner</c>
/// before this class is instantiated.
/// INSERT OR IGNORE semantics are achieved via ON CONFLICT DO NOTHING.
/// </summary>
public sealed class PostgresImportLog : IImportLog
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresImportLog(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Delegates to <see cref="ImportLog.ContentHash"/> — the hash function
    /// is pure and does not depend on the backend.
    /// </summary>
    public static string ContentHash(string text) => ImportLog.ContentHash(text);

    /// <summary>
    /// Returns true if any row in <c>import_log</c> already has the given
    /// <paramref name="contentHash"/>. Used by importers to skip work.
    /// </summary>
    public bool IsAlreadyImported(string contentHash)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM import_log WHERE content_hash = $1 LIMIT 1";
        cmd.Parameters.AddWithValue(contentHash);
        var result = cmd.ExecuteScalar();
        return result is not null && result is not DBNull;
    }

    /// <summary>
    /// INSERT a new import_log row, ignoring duplicates on the primary key.
    /// Idempotent on (sourceTool, sourcePath, contentHash) — re-importing the
    /// same source file with the same content is a no-op.
    /// </summary>
    public void LogImport(
        string sourceTool,
        string sourcePath,
        string contentHash,
        string targetEntryId,
        Tier targetTier,
        ContentType targetType)
    {
        ArgumentNullException.ThrowIfNull(sourceTool);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(contentHash);
        ArgumentNullException.ThrowIfNull(targetEntryId);

        var id = ImportLog.StableId(sourceTool, sourcePath, contentHash);
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO import_log
  (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
VALUES
  ($1, $2, $3, $4, $5, $6, $7, $8)
ON CONFLICT (id) DO NOTHING";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue(sourceTool);
        cmd.Parameters.AddWithValue(sourcePath);
        cmd.Parameters.AddWithValue(contentHash);
        cmd.Parameters.AddWithValue(targetEntryId);
        cmd.Parameters.AddWithValue(ImportLog.TierToString(targetTier));
        cmd.Parameters.AddWithValue(ImportLog.ContentTypeToString(targetType));
        cmd.ExecuteNonQuery();
    }
}
