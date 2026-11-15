using System;
using Microsoft.Data.Sqlite;
using TotalRecall.Core;
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
