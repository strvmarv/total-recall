using System;
using System.Security.Cryptography;
using System.Text;
using TotalRecall.Core;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Telemetry;

/// <summary>
/// Append-only writer for the <c>import_log</c> table. Ports
/// <c>src-ts/importers/import-utils.ts</c>: provides a content-hash helper, a
/// dedupe lookup, and an INSERT-OR-IGNORE writer keyed on a stable
/// MD5(sourceTool:sourcePath:contentHash) id.
///
/// Borrows a non-owning <see cref="MsSqliteConnection"/> the same way
/// <see cref="Storage.SqliteStore"/> does. Caller owns disposal.
/// </summary>
public sealed class ImportLog
{
    private readonly MsSqliteConnection _conn;

    public ImportLog(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        _conn = conn;
    }

    /// <summary>
    /// Lower-case hex SHA-256 of the UTF-8 encoding of <paramref name="text"/>.
    /// Static so callers can hash without instantiating the writer.
    /// </summary>
    public static string ContentHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Returns true if any row in <c>import_log</c> already has the given
    /// <paramref name="contentHash"/>. Used by importers to skip work.
    /// </summary>
    public bool IsAlreadyImported(string contentHash)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM import_log WHERE content_hash = $hash LIMIT 1";
        cmd.Parameters.AddWithValue("$hash", contentHash);
        var result = cmd.ExecuteScalar();
        return result is not null && result is not DBNull;
    }

    /// <summary>
    /// INSERT OR IGNORE a new import_log row. Idempotent on
    /// (sourceTool, sourcePath, contentHash) — re-importing the same source
    /// file with the same content is a no-op.
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

        var id = StableId(sourceTool, sourcePath, contentHash);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO import_log
  (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
VALUES
  ($id, $ts, $tool, $path, $hash, $eid, $tier, $type)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$tool", sourceTool);
        cmd.Parameters.AddWithValue("$path", sourcePath);
        cmd.Parameters.AddWithValue("$hash", contentHash);
        cmd.Parameters.AddWithValue("$eid", targetEntryId);
        cmd.Parameters.AddWithValue("$tier", TierToString(targetTier));
        cmd.Parameters.AddWithValue("$type", ContentTypeToString(targetType));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Stable id derived from <c>MD5(sourceTool:sourcePath:contentHash)</c> —
    /// matches the TS recipe so the same row can be deduped across runtimes.
    /// </summary>
    internal static string StableId(string sourceTool, string sourcePath, string contentHash)
    {
        var input = $"{sourceTool}:{sourcePath}:{contentHash}";
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string TierToString(Tier tier)
    {
        if (tier.IsHot) return "hot";
        if (tier.IsWarm) return "warm";
        if (tier.IsCold) return "cold";
        throw new ArgumentOutOfRangeException(nameof(tier));
    }

    internal static string ContentTypeToString(ContentType type)
    {
        if (type.IsMemory) return "memory";
        if (type.IsKnowledge) return "knowledge";
        throw new ArgumentOutOfRangeException(nameof(type));
    }
}
