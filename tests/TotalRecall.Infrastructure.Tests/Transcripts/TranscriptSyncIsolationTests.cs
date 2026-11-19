using System.Text.RegularExpressions;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using TotalRecall.Infrastructure.Tests.GitSync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The transcript archive must never travel through any sync path (design section 10). One behavioural guard (a joined machine's
/// scan sees the memory row and nothing of the archive) and one static guard (no sync source file mentions the archive).
/// </summary>
public sealed class TranscriptSyncIsolationTests
{
    private const string MemoryId = "9e8d7c6b";
    private const long Now = 1_800_000_000_000L;

    // ---- behavioural ------------------------------------------------------------------------

    private static void SeedArchive(MsSqliteConnection conn)
    {
        TranscriptMigrationRunner.Run(conn);
        for (var s = 1; s <= 3; s++)
        {
            ScanRig.Exec(conn, $"""
                INSERT INTO tx_session (id, host, host_session_id, project_key, project_key_source, kind)
                VALUES ({s}, 'claude-code', 'session-{s}', 'owner/repo', 'remote', 'interactive')
                """);
            for (var c = 0; c < 2; c++)
                ScanRig.Exec(conn, $"""
                    INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, chunk_type, redaction_version, content)
                    VALUES ({s}, {c}, {c}, 'dialogue', 1, 'archived dialogue {s}-{c}')
                    """);
        }
    }

    private static List<string> Column(MsSqliteConnection conn, string sql)
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(r.IsDBNull(0) ? "NULL" : r.GetValue(0).ToString()!);
        return list;
    }

    /// <summary>What a sync path could carry: the ids in git_sync_entry and in sync_queue, in one comparable value.</summary>
    private static (List<string> Entries, List<string> Queue) Observe(ScanRig rig) =>
        (Column(rig.Connection, "SELECT entry_id FROM git_sync_entry ORDER BY entry_id"),
         Column(rig.Connection, "SELECT entity_type || ':' || COALESCE(entity_id, '') FROM sync_queue ORDER BY id"));

    private static ScanRig ArmedRig()
    {
        var rig = new ScanRig();
        try
        {
            rig.AddMemory(Tier.Warm, MemoryId, "a real memory");
            new SyncQueue(rig.Connection).Enqueue("memory", "upsert", MemoryId, "{}");
            SeedArchive(rig.Connection);
            return rig;
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    [Fact]
    public void Scan_OnAJoinedMachineWithAnArchive_StampsTheMemoryAndNothingOfTheArchive()
    {
        using var rig = ArmedRig();

        // the archive really is in this database, so the scanner could have seen it
        Assert.Equal("3", Column(rig.Connection, "SELECT count(*) FROM tx_session")[0]);
        Assert.Equal("6", Column(rig.Connection, "SELECT count(*) FROM tx_chunk")[0]);

        var outcome = rig.Scanner(Now).Scan();
        Assert.IsType<ScanOutcome.Done>(outcome);

        var (entries, queue) = Observe(rig);
        Assert.Equal([MemoryId], entries);              // positive control: the memory IS stamped
        Assert.Equal(["memory:" + MemoryId], queue);    // and exactly that one queued entry exists
        Assert.Equal([MemoryId], rig.Rows().Keys.ToList());

        var backlog = new SyncBacklogReader(rig.Connection).Read();
        Assert.Equal(1, backlog.MemoryUnsynced);
        Assert.Equal(0, backlog.UsageUnsynced + backlog.RetrievalUnsynced + backlog.CompactionUnsynced);
    }

    [Fact]
    public void Control_AnArchiveLookingIdInTheQueue_ChangesTheObservedSet()
    {
        // Proves the behavioural guard can fail: the same observation, after an archive-looking id is enqueued, differs.
        using var rig = ArmedRig();
        Assert.IsType<ScanOutcome.Done>(rig.Scanner(Now).Scan());
        var clean = Observe(rig);

        new SyncQueue(rig.Connection).Enqueue("memory", "upsert", "tx_session:1", "{}");
        var dirty = Observe(rig);

        Assert.NotEqual(clean.Queue, dirty.Queue);
        Assert.Contains("memory:tx_session:1", dirty.Queue);
        Assert.Equal(2, new SyncBacklogReader(rig.Connection).Read().MemoryUnsynced);
    }

    // ---- static -----------------------------------------------------------------------------

    private static readonly string[] Tokens = ["tx_", "Transcript", "Archive"];

    /// <summary>The (path, token) pairs for every token, case-sensitive, that a file's text mentions.</summary>
    internal static List<(string Path, string Token)> FindHits(IEnumerable<(string Path, string Text)> files)
    {
        var hits = new List<(string, string)>();
        foreach (var (path, text) in files)
            foreach (var token in Tokens)
                if (text.Contains(token, StringComparison.Ordinal))
                    hits.Add((path, token));
        return hits;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }

    private static List<(string Path, string Text)> SyncSources()
    {
        var root = FindRepoRoot();
        var files = new List<string>();
        foreach (var relative in new[] { "src/TotalRecall.Infrastructure/Sync", "src/TotalRecall.Infrastructure/GitSync" })
        {
            var dir = Path.Combine(root, relative);
            Assert.True(Directory.Exists(dir), "expected directory " + dir);
            files.AddRange(Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories));
        }
        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "src/TotalRecall.Core"), "Sync*.fs", SearchOption.TopDirectoryOnly));

        return files
            .Select(f => (Rel: Path.GetRelativePath(root, f).Replace('\\', '/'), Full: f))
            .Where(f => !Regex.IsMatch(f.Rel, "/(bin|obj)/"))
            .Select(f => (f.Rel, File.ReadAllText(f.Full)))
            .ToList();
    }

    [Fact]
    public void NoSyncSourceFileMentionsTheArchive()
    {
        var sources = SyncSources();
        Assert.True(sources.Count > 30, "too few sync files scanned: " + sources.Count);
        // SyncSettingsGuard.fs mentions only lowercase "transcripts", which none of the case-sensitive tokens match,
        // so it needs no exclusion and is scanned like every other file.
        Assert.Contains(sources, s => s.Path == "src/TotalRecall.Core/SyncSettingsGuard.fs");
        Assert.Contains(sources, s => s.Path == "src/TotalRecall.Infrastructure/Sync/SyncQueue.cs");
        Assert.Contains(sources, s => s.Path == "src/TotalRecall.Infrastructure/GitSync/GitSyncScanner.cs");

        var hits = FindHits(sources);
        Assert.True(hits.Count == 0, "sync files mention the archive:\n  " + string.Join("\n  ", hits.Select(h => $"{h.Path}: {h.Token}")));
    }

    [Theory]
    [InlineData("SELECT * FROM tx_chunk", "tx_")]
    [InlineData("var store = new TranscriptStore();", "Transcript")]
    [InlineData("// the Archive", "Archive")]
    public void Scan_ReportsAHitOnASyntheticFile(string text, string token)
    {
        var hits = FindHits([("src/TotalRecall.Infrastructure/Sync/Fake.cs", text)]);
        Assert.Equal([("src/TotalRecall.Infrastructure/Sync/Fake.cs", token)], hits);
    }

    [Fact]
    public void Scan_IsCaseSensitive_SoLowercaseTranscriptsIsNotAHit()
    {
        Assert.Empty(FindHits([("a.fs", "| \"transcripts\" -> true  // the transcripts table, an archive")]));
    }
}
