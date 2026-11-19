using System;
using System.IO;
using TotalRecall.Infrastructure.Transcripts;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// A file-backed archive in a temporary directory for the store tests, with raw-SQL inserts (there is no ingest API
/// yet). Disposing closes the store and deletes the directory.
/// </summary>
internal sealed class TranscriptTestDb : IDisposable
{
    public string Dir { get; }
    public string Path { get; }
    public ITranscriptStore Store { get; private set; }

    public TranscriptTestDb()
    {
        Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tr-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Path = System.IO.Path.Combine(Dir, "archive.db");
        var result = TranscriptStore.Open(new TranscriptLocation(Path, IsMainDatabase: false), create: true);
        if (result.Status != TranscriptOpenStatus.Opened || result.Store is null)
            throw new InvalidOperationException($"could not open the test archive: {result.Status} {result.Message}");
        Store = result.Store;
    }

    /// <summary>A second, independent read-write connection to the same file.</summary>
    public MsSqliteConnection Connect() =>
        TotalRecall.Infrastructure.Storage.SqliteConnection.Open(Path, pooling: false);

    public long AddSession(
        string host,
        string hostSessionId,
        string projectKey = "owner/repo",
        string? cwd = null,
        string kind = "interactive",
        string? title = null,
        string? sourcePath = null)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, cwd, kind, title, source_path) " +
            "VALUES ($h, $s, $p, 'remote', $c, $k, $t, $sp); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$s", hostSessionId);
        cmd.Parameters.AddWithValue("$p", projectKey);
        cmd.Parameters.AddWithValue("$c", (object?)cwd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sp", (object?)sourcePath ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public long AddChunk(long sessionId, string content, int exchangeStart = 0, int part = 0)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, redaction_version, content) " +
            "VALUES ($s, $e, $e, $p, 'dialogue', 1, $c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$e", exchangeStart);
        cmd.Parameters.AddWithValue("$p", part);
        cmd.Parameters.AddWithValue("$c", content);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public long Scalar(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public string? Text(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Reads a file that SQLite holds open; empty when it does not exist.</summary>
    public static byte[] ReadShared(string path)
    {
        if (!File.Exists(path)) return Array.Empty<byte>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        Store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }
}
