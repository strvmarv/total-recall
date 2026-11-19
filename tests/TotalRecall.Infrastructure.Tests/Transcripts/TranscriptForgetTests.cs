using System;
using System.Text;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// Delete hygiene (spec section 7): after a forget, none of the forgotten text is left in the raw bytes of the
/// database file, its <c>-wal</c> or its <c>-shm</c>. The markers are lowercase alphanumeric tokens (the text index
/// case-folds and splits on punctuation) and the scan is case-insensitive.
///
/// FTS5 <c>optimize</c>: not needed. These scans pass with the index's persistent <c>secure-delete</c> option
/// (set by the archive migration), <c>PRAGMA secure_delete = ON</c> and <c>wal_checkpoint(TRUNCATE)</c> alone, so the
/// store does not run <c>optimize</c> (the spec's step 2 is omitted).
/// </summary>
public sealed class TranscriptForgetTests : IDisposable
{
    private TranscriptTestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>Replaces the separate archive file with the main database location (main schema plus archive tables).</summary>
    private void UseMainDatabase()
    {
        _db.Dispose();
        _db = new TranscriptTestDb(mainDatabase: true);
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM sqlite_master WHERE name = '_schema_version'"));
    }

    private sealed record Markers(string Content, string Title, string Cwd, string Source);

    private static string Marker(string what) => "zq" + what + Guid.NewGuid().ToString("N");

    private Markers AddMarkedSession(string host, string id, string projectKey)
    {
        var m = new Markers(Marker("content"), Marker("title"), Marker("cwd"), Marker("source"));
        var sid = _db.AddSession(host, id, projectKey: projectKey,
            cwd: "/work/" + m.Cwd, title: "Session " + m.Title, sourcePath: "/logs/" + m.Source + ".jsonl");
        _db.AddChunk(sid, "the user said " + m.Content + " and then more words");
        _db.AddChunk(sid, "a second exchange mentioning " + m.Content.ToUpperInvariant(), exchangeStart: 1);
        return m;
    }

    private string Bytes(params string[] suffixes)
    {
        var sb = new StringBuilder();
        foreach (var suffix in suffixes)
        {
            var raw = TranscriptTestDb.ReadShared(_db.Path + suffix);
            // ASCII case fold; Latin-1 keeps one char per byte.
            for (var i = 0; i < raw.Length; i++)
            {
                var b = raw[i];
                if (b >= (byte)'A' && b <= (byte)'Z') raw[i] = (byte)(b + 32);
            }
            sb.Append(Encoding.Latin1.GetString(raw));
            sb.Append('\0');
        }
        return sb.ToString();
    }

    private string AllFiles() => Bytes("", "-wal", "-shm");

    private static int Count(string haystack, string marker)
    {
        var needle = marker.ToLowerInvariant();
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private void AssertAllPresent(Markers m)
    {
        var all = Bytes("", "-wal");
        Assert.True(Count(all, m.Content) > 0, "content marker missing before the forget");
        Assert.True(Count(all, m.Title) > 0, "title marker missing before the forget");
        Assert.True(Count(all, m.Cwd) > 0, "cwd marker missing before the forget");
        Assert.True(Count(all, m.Source) > 0, "source marker missing before the forget");
    }

    private void AssertNoneLeft(Markers m)
    {
        var all = AllFiles();
        Assert.Equal(0, Count(all, m.Content));
        Assert.Equal(0, Count(all, m.Title));
        Assert.Equal(0, Count(all, m.Cwd));
        Assert.Equal(0, Count(all, m.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForgetSession_LeavesNoTextInAnyFile(bool mainDatabase)
    {
        if (mainDatabase) UseMainDatabase();
        var m = AddMarkedSession("pi", "s1", "owner/repo");
        var keep = AddMarkedSession("pi", "s2", "owner/repo");
        AssertAllPresent(m);

        var result = _db.Store.ForgetSession("pi", "s1");

        Assert.Equal(1, result.Sessions);
        Assert.True(result.WalTruncated);
        Assert.Null(result.Warning);
        AssertNoneLeft(m);
        // the session that was kept is still readable
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM tx_session WHERE host_session_id = 's2'"));
        Assert.Equal(2, _db.Scalar($"SELECT count(*) FROM tx_chunk_fts WHERE tx_chunk_fts MATCH '{keep.Content}'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForgetAll_LeavesNoTextInAnyFile(bool mainDatabase)
    {
        if (mainDatabase) UseMainDatabase();
        var a = AddMarkedSession("pi", "s1", "owner/repo");
        var b = AddMarkedSession("claude-code", "s2", "other/repo");
        AssertAllPresent(a);
        AssertAllPresent(b);

        var result = _db.Store.ForgetAll(DateTimeOffset.UtcNow);

        Assert.Equal(2, result.Sessions);
        Assert.True(result.WalTruncated);
        AssertNoneLeft(a);
        AssertNoneLeft(b);
    }

    [Fact]
    public void ForgetProject_LeavesOnlyTheRootInTheExclusionRecord()
    {
        var m = AddMarkedSession("pi", "s1", "owner/repo");
        AssertAllPresent(m);

        var result = _db.Store.ForgetProject("owner/repo");

        Assert.True(result.WalTruncated);
        var dbAndWal = Bytes("", "-wal");
        Assert.Equal(0, Count(dbAndWal, m.Content));
        Assert.Equal(0, Count(dbAndWal, m.Title));
        Assert.Equal(0, Count(dbAndWal, m.Source));
        Assert.Equal(1, Count(dbAndWal, m.Cwd));
        var shm = Bytes("-shm");
        Assert.Equal(0, Count(shm, m.Content));
        Assert.Equal(0, Count(shm, m.Title));
    }

    [Fact]
    public void BlockedCheckpoint_IsReported_RowsStillDeleted()
    {
        AddMarkedSession("pi", "s1", "owner/repo");

        using var reader = _db.Connect();
        using var tx = reader.BeginTransaction(deferred: true);
        using (var cmd = reader.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT count(*) FROM tx_chunk";
            cmd.ExecuteScalar(); // holds a read snapshot that predates the forget
        }

        var started = DateTime.UtcNow;
        var result = _db.Store.ForgetSession("pi", "s1");
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(1, result.Sessions);
        Assert.False(result.WalTruncated);
        Assert.False(string.IsNullOrEmpty(result.Warning));
        // three retries 200 ms apart with a short busy timeout, not the default 5 s per attempt
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(550), $"retries too fast: {elapsed}");
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"retries too slow: {elapsed}");

        tx.Rollback();
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_session"));
    }
}
