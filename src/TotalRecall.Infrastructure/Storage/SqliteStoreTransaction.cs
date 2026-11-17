using System;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Telemetry;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// An all-or-nothing group of store operations on one connection (git sync).
/// Created by <see cref="SqliteStore.BeginTransaction"/>. Embeddings are
/// computed by the caller before the transaction opens; the transaction
/// never calls the embedder. <see cref="Dispose"/> rolls back unless
/// <see cref="Commit"/> was called, and any call after either throws
/// <see cref="InvalidOperationException"/>. While a transaction is open no
/// other code may use the store's connection.
/// </summary>
public sealed class SqliteStoreTransaction : IDisposable
{
    private readonly SqliteStore _store;
    private readonly MsSqliteConnection _conn;
    private SqliteTransaction? _tx;

    internal SqliteStoreTransaction(
        SqliteStore store, MsSqliteConnection conn, SqliteTransaction tx)
    {
        _store = store;
        _conn = conn;
        _tx = tx;
    }

    /// <summary>
    /// Test seam: called with a step name before each statement group, so a
    /// test can throw at a chosen step and check that nothing was left behind.
    /// </summary>
    internal Action<string>? BeforeStep { get; set; }

    private SqliteTransaction Tx =>
        _tx ?? throw new InvalidOperationException("The store transaction is no longer open");

    /// <summary>The connection the transaction runs on, so the sync tables can be read and written inside it.</summary>
    internal MsSqliteConnection Connection => _conn;

    /// <summary>The open transaction (every command that runs inside it must set <c>Transaction</c> to it).</summary>
    internal SqliteTransaction Transaction => Tx;

    private void Step(string name) => BeforeStep?.Invoke(name);

    /// <summary>
    /// Insert a row (id from <paramref name="opts"/> or a new GUID, the
    /// caller's <c>CreatedAt</c> honored) and, when an embedding is given, its
    /// vector row. Steps: <c>insert-row</c>, <c>insert-vec</c>.
    /// </summary>
    public string Insert(
        Tier tier, ContentType type, InsertEntryOpts opts,
        ReadOnlyMemory<float>? embedding = null)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var tx = Tx;
        var table = MigrationRunner.TableName(tier, type);
        var id = opts.Id ?? Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Step("insert-row");
        var rowid = SqlRows.InsertRow(_conn, tx, table, id, opts, now);
        if (embedding is { } vector)
        {
            Step("insert-vec");
            VecRows.InsertWithOrphanCleanup(
                _conn, tx, MigrationRunner.VecTableName(tier, type), rowid, vector);
        }
        return id;
    }

    /// <summary>Read an entry inside the transaction.</summary>
    public Entry? Get(Tier tier, ContentType type, string id) =>
        SqlRows.ReadRow(_conn, Tx, MigrationRunner.TableName(tier, type), id);

    /// <summary>
    /// Move a row between tiers/types: copy it to the target table as
    /// <c>SqliteStore.Move</c> does (<c>updated_at</c> = now, every other
    /// column kept, <c>sticky</c> not carried), move its vector (the given
    /// embedding, else a copy of the source vector bytes; none when the
    /// source has none and none is given), then delete the source vector and
    /// row. Steps: <c>move-copy</c>, <c>move-vec</c>, <c>delete-vec</c>,
    /// <c>delete-row</c>. Throws <see cref="InvalidOperationException"/> when
    /// the source row does not exist.
    /// </summary>
    public void Move(
        Tier fromTier, ContentType fromType, Tier toTier, ContentType toType,
        string id, ReadOnlyMemory<float>? embedding = null)
    {
        var tx = Tx;
        var fromTable = MigrationRunner.TableName(fromTier, fromType);
        var toTable = MigrationRunner.TableName(toTier, toType);
        var fromVec = MigrationRunner.VecTableName(fromTier, fromType);
        var toVec = MigrationRunner.VecTableName(toTier, toType);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Step("move-copy");
        var sourceRowid = SqlRows.RowId(_conn, tx, fromTable, id);
        var targetRowid = SqlRows.CopyRow(_conn, tx, fromTable, toTable, id, now);

        Step("move-vec");
        // A leftover orphan vector row at the target rowid must never read as
        // the moved entry's vector.
        VecRows.Delete(_conn, tx, toVec, targetRowid);
        var bytes = embedding is { } e
            ? MemoryMarshal.AsBytes(e.Span).ToArray()
            : VecRows.ReadBytes(_conn, tx, fromVec, sourceRowid!.Value);
        if (bytes is not null)
            VecRows.InsertBytes(_conn, tx, toVec, targetRowid, bytes);

        Step("delete-vec");
        VecRows.Delete(_conn, tx, fromVec, sourceRowid!.Value);
        Step("delete-row");
        SqlRows.DeleteRow(_conn, tx, fromTable, id);
    }

    /// <summary>Apply an update to one row. Step: <c>update</c>.</summary>
    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var tx = Tx;
        Step("update");
        SqlRows.UpdateRow(
            _conn, tx, MigrationRunner.TableName(tier, type), id, opts,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Replace the vector of an entry: delete the vector row at the entry's rowid and insert
    /// <paramref name="embedding"/>, so an entry that had none gets one. Throws
    /// <see cref="InvalidOperationException"/> when the entry does not exist. Step: <c>replace-vec</c>.
    /// </summary>
    public void ReplaceEmbedding(
        Tier tier, ContentType type, string id, ReadOnlyMemory<float> embedding)
    {
        var tx = Tx;
        var table = MigrationRunner.TableName(tier, type);
        var rowid = SqlRows.RowId(_conn, tx, table, id)
            ?? throw new InvalidOperationException($"No entry '{id}' in {table}");
        Step("replace-vec");
        var vecTable = MigrationRunner.VecTableName(tier, type);
        VecRows.Delete(_conn, tx, vecTable, rowid);
        VecRows.InsertWithOrphanCleanup(_conn, tx, vecTable, rowid, embedding);
    }

    /// <summary>
    /// Delete a row and its vector row. A missing id is a no-op. Steps:
    /// <c>delete-vec</c>, <c>delete-row</c>.
    /// </summary>
    public void Delete(Tier tier, ContentType type, string id)
    {
        var tx = Tx;
        var table = MigrationRunner.TableName(tier, type);
        var rowid = SqlRows.RowId(_conn, tx, table, id);
        if (rowid is null) return;

        Step("delete-vec");
        VecRows.Delete(_conn, tx, MigrationRunner.VecTableName(tier, type), rowid.Value);
        Step("delete-row");
        SqlRows.DeleteRow(_conn, tx, table, id);
    }

    /// <summary>Set the sticky flag (hot only). Step: <c>set-sticky</c>.</summary>
    public void SetSticky(ContentType type, string id, bool sticky)
    {
        var tx = Tx;
        Step("set-sticky");
        SqlRows.SetStickyRow(_conn, tx, type, id, sticky);
    }

    public bool IsSticky(ContentType type, string id) =>
        SqlRows.IsStickyRow(_conn, Tx, type, id);

    /// <summary>
    /// Record an import in <c>import_log</c> (<c>INSERT OR IGNORE</c>, the same
    /// stable row id as <see cref="ImportLog.LogImport"/>). Step: <c>log-import</c>.
    /// </summary>
    public void LogImport(
        string sourceTool, string sourcePath, string contentHash,
        string targetEntryId, Tier tier, ContentType type)
    {
        ArgumentNullException.ThrowIfNull(sourceTool);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(contentHash);
        ArgumentNullException.ThrowIfNull(targetEntryId);
        var tx = Tx;
        Step("log-import");
        ImportLog.LogImportRow(
            _conn, tx, sourceTool, sourcePath, contentHash, targetEntryId, tier, type);
    }

    public void Commit()
    {
        var tx = Tx;
        tx.Commit();
        Release();
    }

    public void Dispose()
    {
        if (_tx is null) return;
        try { _tx.Rollback(); }
        finally { Release(); }
    }

    private void Release()
    {
        _tx?.Dispose();
        _tx = null;
        _store.EndTransaction();
    }
}
