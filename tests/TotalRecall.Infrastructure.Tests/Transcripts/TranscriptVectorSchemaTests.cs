using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SqliteOpener = TotalRecall.Infrastructure.Storage.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// Archive migration 3 (search the archive, spec section 1): the vector table, the triggers that keep it
/// in step with the chunks and sessions, the embed attempt count, and the runner's lazy write transaction.
/// Not <c>Integration</c>-trait. Rows are written by raw SQL on connections that loaded vec0.
/// </summary>
public sealed class TranscriptVectorSchemaTests : IDisposable
{
    private readonly string _dir;

    public TranscriptVectorSchemaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tr-vec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        MsSqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string NewPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    private static MsSqliteConnection OpenRaw(string path) => SqliteOpener.Open(path, pooling: false);

    private static void Exec(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string? Text(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private static string FtsDataSnapshot(MsSqliteConnection conn) =>
        Text(conn, "SELECT COALESCE(group_concat(id || ':' || hex(block), ','), '') FROM " +
                   "(SELECT id, block FROM tx_chunk_fts_data ORDER BY id)") ?? "";

    private static long AddSession(MsSqliteConnection conn, string hostSessionId, string kind = "interactive", string project = "owner/repo")
    {
        Exec(conn,
            "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, kind) " +
            $"VALUES ('pi', '{hostSessionId}', '{project}', 'remote', '{kind}')");
        return Scalar(conn, "SELECT last_insert_rowid()");
    }

    private static long AddChunk(MsSqliteConnection conn, long sessionId, string content, int start = 0)
    {
        Exec(conn,
            "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, ts, redaction_version, content) " +
            $"VALUES ({sessionId}, {start}, {start}, 0, 'dialogue', '2026-01-01T00:00:00Z', 1, '{content}')");
        return Scalar(conn, "SELECT last_insert_rowid()");
    }

    private static void AddVector(MsSqliteConnection conn, long chunkId, long sessionId, string project, string kind, long ts)
    {
        var rng = new Random(unchecked((int)chunkId));
        var bytes = new byte[384 * sizeof(float)];
        for (var i = 0; i < 384; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float)), (float)(rng.NextDouble() - 0.5));
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tx_chunk_vec (rowid, session_id, project, kind, ts, embedding) VALUES ($r, $s, $p, $k, $t, $e)";
        cmd.Parameters.AddWithValue("$r", chunkId);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", ts);
        cmd.Parameters.AddWithValue("$e", bytes);
        cmd.ExecuteNonQuery();
        Exec(conn, $"UPDATE tx_chunk SET embedded = 1 WHERE id = {chunkId}");
    }

    private static long VectorCount(MsSqliteConnection conn, string where = "1 = 1") =>
        Scalar(conn, $"SELECT COUNT(*) FROM tx_chunk_vec WHERE {where}");

    private MsSqliteConnection Migrated(out string path)
    {
        path = NewPath();
        var conn = OpenRaw(path);
        TranscriptMigrationRunner.Run(conn);
        return conn;
    }

    [Fact]
    public void DeleteChunk_RemovesItsVector()
    {
        using var conn = Migrated(out _);
        var sid = AddSession(conn, "s1");
        var a = AddChunk(conn, sid, "alpha");
        var b = AddChunk(conn, sid, "beta", start: 1);
        AddVector(conn, a, sid, "owner/repo", "interactive", 1);
        AddVector(conn, b, sid, "owner/repo", "interactive", 1);

        Exec(conn, $"DELETE FROM tx_chunk WHERE id = {a}");

        Assert.Equal(0, VectorCount(conn, $"rowid = {a}"));
        Assert.Equal(1, VectorCount(conn, $"rowid = {b}"));
    }

    [Fact]
    public void DeleteSession_Cascades_RemovesAllItsVectors()
    {
        using var conn = Migrated(out _);
        var s1 = AddSession(conn, "s1");
        var s2 = AddSession(conn, "s2");
        for (var i = 0; i < 3; i++) AddVector(conn, AddChunk(conn, s1, "one" + i, start: i), s1, "owner/repo", "interactive", 1);
        var keep = AddChunk(conn, s2, "keeper");
        AddVector(conn, keep, s2, "owner/repo", "interactive", 1);
        Assert.Equal(4, VectorCount(conn));

        Exec(conn, $"DELETE FROM tx_session WHERE id = {s1}");

        Assert.Equal(1, VectorCount(conn));
        Assert.Equal(1, VectorCount(conn, $"rowid = {keep}"));
    }

    [Fact]
    public void UpdateContent_RemovesVector_ResetsEmbeddedAndAttempts()
    {
        using var conn = Migrated(out _);
        var sid = AddSession(conn, "s1");
        var cid = AddChunk(conn, sid, "before");
        AddVector(conn, cid, sid, "owner/repo", "interactive", 1);
        Exec(conn, $"UPDATE tx_chunk SET embedded = 1, embed_attempts = 2 WHERE id = {cid}");

        Exec(conn, $"UPDATE tx_chunk SET content = 'x' WHERE id = {cid}");

        Assert.Equal(0, VectorCount(conn, $"rowid = {cid}"));
        Assert.Equal(0, Scalar(conn, $"SELECT embedded FROM tx_chunk WHERE id = {cid}"));
        Assert.Equal(0, Scalar(conn, $"SELECT embed_attempts FROM tx_chunk WHERE id = {cid}"));
    }

    [Fact]
    public void UpdateEmbeddedOnly_KeepsVectorAndFtsRowCount()
    {
        using var conn = Migrated(out _);
        var sid = AddSession(conn, "s1");
        var cid = AddChunk(conn, sid, "stayputword");
        AddVector(conn, cid, sid, "owner/repo", "interactive", 1);
        // Every write to the FTS5 index (a 'delete' plus a re-insert of the same row included) flushes a new
        // segment into the _data shadow table, so an unchanged _data table proves the index was not touched.
        // A row count of _docsize could not tell: a delete and re-insert of one rowid leaves it equal.
        var ftsData = FtsDataSnapshot(conn);

        Exec(conn, $"UPDATE tx_chunk SET embedded = 1 WHERE id = {cid}");
        Exec(conn, $"UPDATE tx_chunk SET embed_attempts = 1 WHERE id = {cid}");

        Assert.Equal(1, VectorCount(conn, $"rowid = {cid}"));
        Assert.Equal(ftsData, FtsDataSnapshot(conn));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk_fts WHERE tx_chunk_fts MATCH 'stayputword'"));
        Assert.Equal(1, Scalar(conn, $"SELECT embedded FROM tx_chunk WHERE id = {cid}"));
        Assert.Equal(1, Scalar(conn, $"SELECT embed_attempts FROM tx_chunk WHERE id = {cid}"));
        Assert.Contains("UPDATE OF content",
            Text(conn, "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'tx_chunk_fts_au'"));
    }

    [Fact]
    public void SessionKindUpdate_ChangesVectorKind()
    {
        using var conn = Migrated(out _);
        var s1 = AddSession(conn, "s1");
        var s2 = AddSession(conn, "s2");
        var a = AddChunk(conn, s1, "a");
        var b = AddChunk(conn, s1, "b", start: 1);
        var other = AddChunk(conn, s2, "c");
        AddVector(conn, a, s1, "owner/repo", "interactive", 1);
        AddVector(conn, b, s1, "owner/repo", "interactive", 1);
        AddVector(conn, other, s2, "owner/repo", "interactive", 1);

        Exec(conn, $"UPDATE tx_session SET kind = 'automated' WHERE id = {s1}");

        Assert.Equal(2, VectorCount(conn, "kind = 'automated'"));
        Assert.Equal(1, VectorCount(conn, $"kind = 'interactive' AND rowid = {other}"));
    }

    [Fact]
    public void SessionProjectUpdate_ChangesVectorProject()
    {
        using var conn = Migrated(out _);
        var s1 = AddSession(conn, "s1");
        var s2 = AddSession(conn, "s2");
        var a = AddChunk(conn, s1, "a");
        var other = AddChunk(conn, s2, "c");
        AddVector(conn, a, s1, "owner/repo", "interactive", 1);
        AddVector(conn, other, s2, "owner/repo", "interactive", 1);

        Exec(conn, $"UPDATE tx_session SET project_key = 'new/place' WHERE id = {s1}");

        Assert.Equal("new/place", Text(conn, $"SELECT project FROM tx_chunk_vec WHERE rowid = {a}"));
        Assert.Equal("owner/repo", Text(conn, $"SELECT project FROM tx_chunk_vec WHERE rowid = {other}"));
    }

    [Fact]
    public void SessionUpdateWithSameValues_DoesNotTouchVectors()
    {
        using var conn = Migrated(out _);
        var sid = AddSession(conn, "s1");
        var cid = AddChunk(conn, sid, "a");
        AddVector(conn, cid, sid, "owner/repo", "interactive", 1);
        Exec(conn, $"UPDATE tx_chunk_vec SET kind = 'sentinel', project = 'sentinel' WHERE rowid = {cid}");

        // An upsert that sets the same values must not fire the vector update (the WHEN clause).
        Exec(conn, $"UPDATE tx_session SET kind = 'interactive', project_key = 'owner/repo', title = 't' WHERE id = {sid}");

        Assert.Equal("sentinel", Text(conn, $"SELECT kind FROM tx_chunk_vec WHERE rowid = {cid}"));
        Assert.Equal("sentinel", Text(conn, $"SELECT project FROM tx_chunk_vec WHERE rowid = {cid}"));
    }

    [Fact]
    public void ChunkIdReused_AfterDelete_MeetsNoOldVector()
    {
        using var conn = Migrated(out _);
        var sid = AddSession(conn, "s1");
        var cid = AddChunk(conn, sid, "first");
        AddVector(conn, cid, sid, "owner/repo", "interactive", 1);

        // INTEGER PRIMARY KEY without AUTOINCREMENT reuses the largest id once that row is gone.
        Exec(conn, $"DELETE FROM tx_chunk WHERE id = {cid}");
        var again = AddChunk(conn, sid, "second");

        Assert.Equal(cid, again);
        Assert.Equal(0, VectorCount(conn, $"rowid = {again}"));
        Assert.Equal(0, Scalar(conn, $"SELECT embedded FROM tx_chunk WHERE id = {again}"));
    }

    [Fact]
    public void Migration_FromVersion1_ToCurrent_AppliesBoth()
    {
        var path = NewPath();
        using var conn = OpenRaw(path);
        TranscriptMigrationRunner.Run(conn, 1);
        var sid = AddSession(conn, "s1");
        AddChunk(conn, sid, "legacywordone");

        TranscriptMigrationRunner.Run(conn);

        Assert.Equal(3, TranscriptMigrationRunner.CurrentVersion(conn));
        foreach (var name in new[]
                 {
                     "tx_chunk_vec", "tx_chunk_vec_ad", "tx_chunk_vec_au", "tx_session_vec_ku", "tx_chunk_reset_au",
                     "tx_chunk_fts_au", "idx_tx_chunk_unembedded", "idx_tx_session_project_kind",
                 })
            Assert.Equal(1, Scalar(conn, $"SELECT COUNT(*) FROM sqlite_master WHERE name = '{name}'"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('tx_session') WHERE name = 'project_root'"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('tx_chunk') WHERE name = 'embed_attempts'"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM tx_session"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk_fts WHERE tx_chunk_fts MATCH 'legacywordone'"));
    }

    [Fact]
    public void Migration3_OnVersion2Archive_ResetsEmbeddedToZero_AndKeepsRows()
    {
        var path = NewPath();
        using var conn = OpenRaw(path);
        TranscriptMigrationRunner.Run(conn, 2);
        var sid = AddSession(conn, "s1");
        var cid = AddChunk(conn, sid, "keptwordtwo");
        Exec(conn, $"UPDATE tx_chunk SET embedded = 1 WHERE id = {cid}");
        Assert.Equal(1, Scalar(conn, $"SELECT embedded FROM tx_chunk WHERE id = {cid}"));

        TranscriptMigrationRunner.Run(conn);

        Assert.Equal(3, TranscriptMigrationRunner.CurrentVersion(conn));
        Assert.Equal(0, Scalar(conn, $"SELECT embedded FROM tx_chunk WHERE id = {cid}"));
        Assert.Equal(0, Scalar(conn, $"SELECT embed_attempts FROM tx_chunk WHERE id = {cid}"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk_fts WHERE tx_chunk_fts MATCH 'keptwordtwo'"));
    }

    [Fact]
    public void Run_WhenUpToDate_TakesNoWriteLock()
    {
        var path = NewPath();
        using (var setup = OpenRaw(path)) TranscriptMigrationRunner.Run(setup);

        using var holder = OpenRaw(path);
        Exec(holder, "BEGIN IMMEDIATE");
        using var runner = OpenRaw(path);
        var sw = Stopwatch.StartNew();

        TranscriptMigrationRunner.Run(runner);

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Run waited {sw.Elapsed} behind a write lock");
        Exec(holder, "ROLLBACK");
        Assert.Equal(3, TranscriptMigrationRunner.CurrentVersion(runner));
    }

    [Fact]
    public async Task Run_PendingMigration_BehindAWriter_WaitsAndMigrates()
    {
        // A second process opens a version-2 archive while another connection holds the write lock with a write
        // of its own. A deferred BEGIN would read the version at the old snapshot and then fail at once on its first
        // write with "database is locked" (WAL: a stale read snapshot cannot be upgraded, the busy handler is not
        // used). BEGIN IMMEDIATE waits behind the busy timeout and then migrates.
        var path = NewPath();
        using (var setup = OpenRaw(path)) TranscriptMigrationRunner.Run(setup, 2);

        using var holder = OpenRaw(path);
        Exec(holder, "BEGIN IMMEDIATE");
        Exec(holder, "INSERT INTO tx_state (key, value) VALUES ('holder', 'x')");

        var run = Task.Run(() =>
        {
            using var conn = OpenRaw(path);
            TranscriptMigrationRunner.Run(conn);
            return TranscriptMigrationRunner.CurrentVersion(conn);
        });
        await Task.Delay(500);
        Exec(holder, "COMMIT");

        Assert.Equal(3, await run);
        using var check = OpenRaw(path);
        Assert.Equal(3, TranscriptMigrationRunner.CurrentVersion(check));
        Assert.Equal("x", Text(check, "SELECT value FROM tx_state WHERE key = 'holder'"));
    }

    [Fact]
    public async Task Run_PendingMigration_StillAtomic()
    {
        var path = NewPath();
        using (var setup = OpenRaw(path)) TranscriptMigrationRunner.Run(setup, 2);

        var errors = new List<Exception>();
        var tasks = new Task[2];
        using var gate = new System.Threading.Barrier(2);
        for (var i = 0; i < 2; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    using var conn = OpenRaw(path);
                    gate.SignalAndWait();
                    TranscriptMigrationRunner.Run(conn);
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            });
        }
        await Task.WhenAll(tasks);

        Assert.Empty(errors);
        using var check = OpenRaw(path);
        Assert.Equal(3, TranscriptMigrationRunner.CurrentVersion(check));
        Assert.Equal(1, Scalar(check, "SELECT COUNT(*) FROM tx_schema_version WHERE version = 3"));
        foreach (var name in new[] { "tx_chunk_vec", "tx_chunk_vec_ad", "tx_chunk_vec_au", "tx_session_vec_ku", "tx_chunk_reset_au", "idx_tx_chunk_unembedded" })
            Assert.Equal(1, Scalar(check, $"SELECT COUNT(*) FROM sqlite_master WHERE name = '{name}'"));
    }
}
