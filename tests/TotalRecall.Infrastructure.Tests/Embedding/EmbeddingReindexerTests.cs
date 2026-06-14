using System;
using System.IO;
using System.Threading;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class EmbeddingReindexerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MsSqliteConnection _conn;
    private readonly SqliteStore _store;
    private readonly VectorSearch _vec;
    private readonly IEmbedder _newEmbedder = new ConstantEmbedder(0.5f);

    public EmbeddingReindexerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "tr-reindex-" + Guid.NewGuid().ToString("N") + ".db");
        _conn = SqliteConnection.Open(_dbPath);
        MigrationRunner.RunMigrations(_conn);
        _store = new SqliteStore(_conn);
        _vec = new VectorSearch(_conn);
    }

    public void Dispose()
    {
        _conn.Dispose();
        Cleanup(_dbPath);
    }

    // --- Reindex (legacy List-based pass) --------------------------------

    [Fact]
    public void Reindex_RewritesVectorsForAllEntries_AndReturnsCount()
    {
        var oldE = new ConstantEmbedder(1.0f);
        _store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
            new InsertEntryOpts("first fact"), oldE.Embed("first fact"));
        _store.InsertWithEmbedding(Tier.Cold, ContentType.Memory,
            new InsertEntryOpts("second fact"), oldE.Embed("second fact"));

        var reindexer = new EmbeddingReindexer(_store, _vec, _newEmbedder);
        int n = reindexer.Reindex(progress: null);

        Assert.Equal(2, n);
    }

    // --- RunBatched -------------------------------------------------------

    [Fact]
    public void RunBatched_ReindexesEveryRow_AndProgressMatches()
    {
        var n = ReindexHarness.Seed(_store, _vec, warmMemories: 600, coldKnowledge: 50);
        var progress = new ReindexProgress();
        progress.BeginRunning(n, "new", 0);

        int rewritten = EmbeddingReindexer.RunBatched(
            _conn, _store, _vec, _newEmbedder, progress, CancellationToken.None, log: null, batchSize: 256);

        Assert.Equal(n, rewritten);
        Assert.Equal(n, progress.Done);
    }

    [Fact]
    public void RunBatched_ResumesFromCursor_SkippingCompletedRows()
    {
        ReindexHarness.Seed(_store, _vec, warmMemories: 500, coldKnowledge: 0);
        _store.SetMeta(EmbeddingReindexer.CursorTargetKey, _newEmbedder.Descriptor.Model);
        _store.SetMeta(EmbeddingReindexer.CursorPairKey, PairIndex(Tier.Warm, ContentType.Memory).ToString());
        _store.SetMeta(EmbeddingReindexer.CursorRowidKey, "300");

        int rewritten = EmbeddingReindexer.RunBatched(
            _conn, _store, _vec, _newEmbedder, new ReindexProgress(), CancellationToken.None, null, 256);

        // Only rows with rowid > 300 are re-embedded.
        Assert.True(rewritten < 500 && rewritten > 0,
            $"expected partial re-embed (0 < rewritten < 500) but got {rewritten}");
    }

    [Fact]
    public void RunBatched_CursorResetWhenTargetModelDiffers()
    {
        ReindexHarness.Seed(_store, _vec, warmMemories: 500, coldKnowledge: 0);
        // Cursor belongs to a DIFFERENT (interrupted) migration. RunBatched must
        // discard it and re-embed everything from the start.
        _store.SetMeta(EmbeddingReindexer.CursorTargetKey, "some-other-model");
        _store.SetMeta(EmbeddingReindexer.CursorPairKey, PairIndex(Tier.Warm, ContentType.Memory).ToString());
        _store.SetMeta(EmbeddingReindexer.CursorRowidKey, "300");

        int rewritten = EmbeddingReindexer.RunBatched(
            _conn, _store, _vec, _newEmbedder, new ReindexProgress(), CancellationToken.None, null, 256);

        Assert.Equal(500, rewritten);
        Assert.Equal(_newEmbedder.Descriptor.Model, _store.GetMeta(EmbeddingReindexer.CursorTargetKey));
    }

    [Fact]
    public void RunBatched_CommitsPerBatch_CursorAdvances()
    {
        ReindexHarness.Seed(_store, _vec, warmMemories: 300, coldKnowledge: 0);

        EmbeddingReindexer.RunBatched(
            _conn, _store, _vec, _newEmbedder, new ReindexProgress(),
            CancellationToken.None, null, batchSize: 100);

        // Cursor was written (coordinator clears it later).
        Assert.NotNull(_store.GetMeta(EmbeddingReindexer.CursorRowidKey));
    }

    [Fact]
    public void RunBatched_Cancellation_ThrowsBetweenBatches_LeavesResumableCursor()
    {
        ReindexHarness.Seed(_store, _vec, warmMemories: 1000, coldKnowledge: 0);
        using var cts = new CancellationTokenSource();
        // Cancels the token after 150 embeds — i.e. partway through the 2nd
        // batch of 100, so exactly 1 batch (100 rows) has committed by the time
        // the NEXT batch's top-of-loop cancellation check fires.
        var cancelling = new CancelAfterEmbedder(_newEmbedder, cts, afterCalls: 150);

        // Cancellation contract: RunBatched THROWS OperationCanceledException after
        // the in-progress batch commits, so the persisted cursor points at fully
        // committed data and the next invocation resumes cleanly.
        Assert.Throws<OperationCanceledException>(() =>
            EmbeddingReindexer.RunBatched(
                _conn, _store, _vec, cancelling, new ReindexProgress(), cts.Token, null, 100));

        // A resumable cursor was left behind, targeting the in-flight model.
        Assert.Equal(_newEmbedder.Descriptor.Model, _store.GetMeta(EmbeddingReindexer.CursorTargetKey));
        Assert.NotNull(_store.GetMeta(EmbeddingReindexer.CursorPairKey));
        Assert.NotNull(_store.GetMeta(EmbeddingReindexer.CursorRowidKey));

        // At least one batch committed (cursor at rowid 200) but not the whole run.
        long cursorRowid = long.Parse(_store.GetMeta(EmbeddingReindexer.CursorRowidKey)!);
        Assert.True(cursorRowid >= 100 && cursorRowid < 1000,
            $"expected a partial cursor (100 <= rowid < 1000) but got {cursorRowid}");
    }

    [Fact]
    public void RunBatched_SkipsRowDeletedMidBatch_DoesNotThrow()
    {
        ReindexHarness.Seed(_store, _vec, warmMemories: 10, coldKnowledge: 0);
        var hook = new DeleteOnFirstEmbed(_newEmbedder, _store, _conn, Tier.Warm, ContentType.Memory);

        var ex = Record.Exception(() =>
            EmbeddingReindexer.RunBatched(
                _conn, _store, _vec, hook, new ReindexProgress(),
                CancellationToken.None, null, 256));

        Assert.Null(ex);
    }

    // --- helpers ----------------------------------------------------------

    /// <summary>Index of a (tier, type) pair in <see cref="TierNames.AllTablePairs"/>.</summary>
    private static int PairIndex(Tier tier, ContentType type)
    {
        var pairs = TierNames.AllTablePairs;
        for (int i = 0; i < pairs.Length; i++)
            if (pairs[i].Tier.Equals(tier) && pairs[i].Type.Equals(type))
                return i;
        throw new ArgumentException($"({tier},{type}) is not in TierNames.AllTablePairs");
    }

    // The sqlite-vec native handle + Microsoft.Data.Sqlite pooling can keep the
    // file mapped briefly past Dispose on Windows; drain the pool, then delete
    // the db (and WAL/SHM sidecars) best-effort so a transient lock never fails
    // an otherwise-passing test.
    private static void Cleanup(string dbPath)
    {
        MsSqliteConnection.ClearAllPools();
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (IOException) { }
        }
    }
}

/// <summary>
/// Seeds a populated store + vec index for RunBatched tests. Uses an old
/// (content-dependent) embedder so the seeded vectors differ from the
/// re-embed target, mirroring the "model changed under an existing DB" case.
/// </summary>
internal static class ReindexHarness
{
    private static readonly IEmbedder OldEmbedder = new FakeEmbedder();

    public static int Seed(SqliteStore store, VectorSearch vec, int warmMemories, int coldKnowledge)
    {
        for (int i = 0; i < warmMemories; i++)
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts($"warm memory {i}"), OldEmbedder.Embed($"warm memory {i}"));

        for (int i = 0; i < coldKnowledge; i++)
            store.InsertWithEmbedding(Tier.Cold, ContentType.Knowledge,
                new InsertEntryOpts($"cold knowledge {i}"), OldEmbedder.Embed($"cold knowledge {i}"));

        return warmMemories + coldKnowledge;
    }
}

/// <summary>
/// Decorator that cancels <paramref name="cts"/> after a fixed number of
/// <see cref="Embed"/> calls, so RunBatched observes the cancellation at the
/// top of the NEXT batch (between batches).
/// </summary>
internal sealed class CancelAfterEmbedder : IEmbedder
{
    private readonly IEmbedder _inner;
    private readonly CancellationTokenSource _cts;
    private readonly int _afterCalls;
    private int _calls;

    public CancelAfterEmbedder(IEmbedder inner, CancellationTokenSource cts, int afterCalls)
    {
        _inner = inner;
        _cts = cts;
        _afterCalls = afterCalls;
    }

    public EmbedderDescriptor Descriptor => _inner.Descriptor;

    public float[] Embed(string text)
    {
        var v = _inner.Embed(text);
        if (++_calls == _afterCalls)
            _cts.Cancel();
        return v;
    }
}

/// <summary>
/// Decorator that, on its FIRST <see cref="Embed"/> call, deletes the content
/// row for the text it was just handed (directly via SQL so the in-flight batch
/// is unaffected until the write phase). When RunBatched later tries to
/// re-insert that row's vector, <see cref="IVectorSearch.InsertEmbedding"/>
/// throws <see cref="InvalidOperationException"/> — which RunBatched must swallow.
/// </summary>
internal sealed class DeleteOnFirstEmbed : IEmbedder
{
    private readonly IEmbedder _inner;
    private readonly SqliteStore _store;
    private readonly MsSqliteConnection _conn;
    private readonly Tier _tier;
    private readonly ContentType _type;
    private bool _deleted;

    public DeleteOnFirstEmbed(IEmbedder inner, SqliteStore store, MsSqliteConnection conn, Tier tier, ContentType type)
    {
        _inner = inner;
        _store = store;
        _conn = conn;
        _tier = tier;
        _type = type;
    }

    public EmbedderDescriptor Descriptor => _inner.Descriptor;

    public float[] Embed(string text)
    {
        if (!_deleted)
        {
            _deleted = true;
            // Delete the row matching this content so the subsequent vec insert
            // for it raises InvalidOperationException ("entry not found").
            var id = _store.FindByContent(_tier, _type, text);
            if (id is not null)
                _store.Delete(_tier, _type, id);
        }
        return _inner.Embed(text);
    }
}
